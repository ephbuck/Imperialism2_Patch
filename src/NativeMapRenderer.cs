using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Imp2Net
{
    // The original UI remains logical 800x600. Only map-owned rendering uses
    // physical pixels. All addresses below belong to the researched GOG 1.03 image.
    public static class NativeMapRenderer
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        delegate void Draw(IntPtr self, IntPtr rect);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        delegate void Selection(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        delegate void Idle(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        delegate void MiniSize(IntPtr self, IntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void CopyBits(IntPtr source, IntPtr destination, IntPtr sr, IntPtr dr, int mode, IntPtr mask);

        static readonly Draw drawHook = OnDraw;
        static readonly CopyBits copyHook = OnMapCopy;
        static readonly Idle focusHook = OnFocus, transparentHook = OnTransparent;
        static readonly MiniSize miniHook = OnMiniSize;
        static readonly Dictionary<int, Selection> selections = new Dictionary<int, Selection>();
        static readonly List<Delegate> roots = new List<Delegate>();
        static Draw originalDraw;
        static Idle originalFocus, originalTransparent;
        static MiniSize originalMini;
        static CopyBits originalCopy;
        static IntPtr active, mini;
        static IntPtr rectangle = Marshal.AllocHGlobal(16);
        static bool installed, drawing, sizeDirty = true;
        static int width = 576, height = 512;
        public static int MapWidth { get { return width; } }
        public static int MapHeight { get { return height; } }
        public static IntPtr ActiveMap { get { return active; } }
        public static long Frames { get; private set; }

        public static void Log(string value)
        {
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "windowed-map.log"),
                DateTime.Now.ToString("HH:mm:ss.fff ") + value + Environment.NewLine); } catch { }
        }

        public static bool IsActive()
        {
            return active != IntPtr.Zero && NativeMapInput.IsActiveMapVisible;
        }

        public static void Initialize()
        {
            if (installed) return;
            NativePatch.Require(0x55d070, new byte[] { 0x64,0xa1,0,0,0,0,0x6a,0xff });
            NativePatch.Require(0x49435a, new byte[] { 0x0f,0x94,0xc1 });
            NativePatch.Require(0x532eac, new byte[] { 0xa1,0x88,0xbc,0x75,0,0xba,0xc0,0xff,0xff,0xff,0x89,0x44,0x24,8,
                0xa1,0x8c,0xbc,0x75,0,0x83,0xc0,0x40,0x89,0x54,0x24,0,0x89,0x44,0x24,0x0c });
            foreach (int slot in new int[] { 0x703a14,0x703ca4,0x703f34 })
                NativePatch.Require(slot, BitConverter.GetBytes(0x409ade));
            NativePatch.RequireCall(0x55d889, 0x404e8f);
            RequireGeometry();
            // A maximum-sized guarded tactical target is allocated once by the
            // original engine and reused across resize. No live GWorld is freed.
            NativePatch.Write(0x532eac, new byte[] { 0xba,0xc0,0xff,0xff,0xff,0x89,0x54,0x24,0,
                0x89,0x54,0x24,4,0xc7,0x44,0x24,8,0,0x0f,0,0,0xc7,0x44,0x24,0x0c,0xb0,8,0,0,0x90 });
            // Ask DirectDraw for DDSCL_NORMAL; never switch the desktop mode.
            NativePatch.Write(0x49435a, new byte[] { 0x31,0xc9,0x90 });
            originalDraw = (Draw)Marshal.GetDelegateForFunctionPointer((IntPtr)0x409ade, typeof(Draw));
            originalCopy = (CopyBits)Marshal.GetDelegateForFunctionPointer((IntPtr)0x404e8f, typeof(CopyBits));
            foreach (int slot in new int[] { 0x703a14,0x703ca4,0x703f34 }) NativePatch.Pointer(slot, drawHook);
            NativePatch.Write(0x55d889, NativePatch.Branch(0xe8, 0x55d889, Marshal.GetFunctionPointerForDelegate(copyHook).ToInt32()));
            // Keep the distinct Old/New World selection implementations.
            int[] selectionSlots = { 0x703ab8,0x703d48,0x703fd8 };
            int[] selectionTargets = { 0x402009,0x403616,0x402009 };
            for (int i = 0; i < selectionSlots.Length; i++)
            {
                int index = i;
                NativePatch.Require(selectionSlots[i], BitConverter.GetBytes(selectionTargets[i]));
                selections[index] = (Selection)Marshal.GetDelegateForFunctionPointer((IntPtr)selectionTargets[i], typeof(Selection));
                Selection callback = delegate(IntPtr map) { OnSelection(map, index); };
                roots.Add(callback); NativePatch.Pointer(selectionSlots[i], callback);
            }
            originalFocus = (Idle)Marshal.GetDelegateForFunctionPointer(NativePatch.Trampoline(0x4b86e0,
                new byte[] { 0x64,0xa1,0,0,0,0 }, focusHook), typeof(Idle));
            originalTransparent = (Idle)Marshal.GetDelegateForFunctionPointer(NativePatch.Trampoline(0x4b8c10,
                new byte[] { 0x6a,0xff,0x68,0xf8,0x3c,0x6d,0 }, transparentHook), typeof(Idle));
            originalMini = (MiniSize)Marshal.GetDelegateForFunctionPointer(NativePatch.Trampoline(0x60ecd0,
                new byte[] { 0x8b,0x44,0x24,4,0x8b,0x10 }, miniHook), typeof(MiniSize));
            WindowedMapBridge.MapActive = IsActive;
            WindowedMapBridge.SizeChanged = Resize;
            WindowedMapBridge.RenderMap = Redraw;
            NativeMapInput.MapDestroyed = delegate(IntPtr map) { if (active == map) { active = IntPtr.Zero; mini = IntPtr.Zero; } };
            Resize(WindowedMapBridge.ClientWidth, WindowedMapBridge.ClientHeight);
            installed = true;
            Log("Native map renderer installed; bounded 3840x2160 presentation, native 64px tile pitch.");
        }

        static void RequireGeometry()
        {
            NativePatch.Require(0x55a753, new byte[] { 0x66,0x81,0xf9,0x40,2 });
            NativePatch.Require(0x55a75a, new byte[] { 0x66,0x3d,0,2 });
            NativePatch.Require(0x55aa9b, new byte[] { 0x66,0x83,0xf9,0x34 });
            NativePatch.Require(0x55aaa1, new byte[] { 0xb9,0x34,0,0,0 });
            NativePatch.Require(0x55a8a2, new byte[] { 0x83,0xc0,0xfc });
            NativePatch.Require(0x55a95a, new byte[] { 0x83,0xc6,4 });
            foreach (int address in new int[] {0x55aaee,0x55a8da})
                NativePatch.Require(address, BitConverter.GetBytes(address == 0x55aaee ? 576 : 575));
            foreach (int address in new int[] {0x55aaf6,0x55a8e2})
                NativePatch.Require(address, BitConverter.GetBytes(address == 0x55aaf6 ? 512 : 511));
            NativePatch.Require(0x55ab1a, BitConverter.GetBytes(640));
            NativePatch.Require(0x55ab1f, BitConverter.GetBytes(576));
        }

        public static void Resize(int clientWidth, int clientHeight)
        {
            NativeMapInput.SetViewportSize(clientWidth, clientHeight);
            width = Math.Max(576, Math.Min(3616, clientWidth - 224));
            height = Math.Max(512, Math.Min(2072, clientHeight - 88));
            NativePatch.Write(0x55a756, BitConverter.GetBytes((short)width));
            NativePatch.Write(0x55a75c, BitConverter.GetBytes((short)height));
            byte lastRow = (byte)Math.Max(0, (3840 - height) / 64);
            NativePatch.Write(0x55aa9e, new byte[] { lastRow });
            NativePatch.Write(0x55aaa2, BitConverter.GetBytes((int)lastRow));
            byte halfRows = (byte)(height / 128);
            NativePatch.Write(0x55a8a4, new byte[] { unchecked((byte)-halfRows) });
            NativePatch.Write(0x55a95c, new byte[] { halfRows });
            NativePatch.Write(0x55aaee, BitConverter.GetBytes(width));
            NativePatch.Write(0x55aaf6, BitConverter.GetBytes(height));
            NativePatch.Write(0x55a8da, BitConverter.GetBytes(width - 1));
            NativePatch.Write(0x55a8e2, BitConverter.GetBytes(height - 1));
            NativePatch.Write(0x55ab1a, BitConverter.GetBytes(width + 64));
            NativePatch.Write(0x55ab1f, BitConverter.GetBytes(height + 64));
            sizeDirty = true;
            Log("Viewport " + width + "x" + height + "; last camera row=" + lastRow);
        }

        public static void Redraw()
        {
            if (!drawing && IsActive()) OnDraw(active, rectangle);
        }

        static void OnDraw(IntPtr map, IntPtr dirty)
        {
            if (drawing) { originalDraw(map, dirty); return; }
            active = map;
            NativeMapInput.SetActiveMap(map);
            Marshal.WriteInt32(map, 0x34, width); Marshal.WriteInt32(map, 0x38, height);
            Marshal.WriteInt32((IntPtr)0x75c048, width / 64);
            if (sizeDirty)
            {
                int camera = Marshal.ReadInt32(map, 0x68);
                int max = ((3840 - height) / 64) * 64;
                if (camera > max) Marshal.WriteInt32(map, 0x68, max);
                if (mini != IntPtr.Zero) UpdateMini(mini);
                sizeDirty = false;
            }
            Rect full = new Rect { Right = width, Bottom = height };
            Marshal.StructureToPtr(full, rectangle, false);
            drawing = true;
            WindowedMapBridge.BeginMapDraw();
            try { originalDraw(map, rectangle); Frames++; }
            finally { WindowedMapBridge.EndMapDraw(); drawing = false; }
        }

        static void OnMapCopy(IntPtr source, IntPtr destination, IntPtr sourceRect, IntPtr destinationRect, int mode, IntPtr mask)
        {
            // CopyBits takes PixMap handles. The native DIB owns pitch, masks and
            // orientation; StretchDIBits performs the 16-to-32 conversion at 1:1.
            IntPtr dib = Marshal.ReadIntPtr(source, 0x1c);
            if (dib == IntPtr.Zero) throw new InvalidOperationException("Map pixmap has no native DIB.");
            IntPtr bits = Marshal.ReadIntPtr(dib, 0x0c), bmi = Marshal.ReadIntPtr(dib, 0x10);
            Rect sr = (Rect)Marshal.PtrToStructure(sourceRect, typeof(Rect));
            Rect dr = (Rect)Marshal.PtrToStructure(destinationRect, typeof(Rect));
            WindowedMapBridge.PresentNativeMapDib(bits, bmi, sr.Left, sr.Top, sr.Right-sr.Left, sr.Bottom-sr.Top,
                10 + dr.Left, 58 + dr.Top);
        }

        static void OnSelection(IntPtr map, int index)
        {
            WindowedMapBridge.BeginMapDraw();
            try { selections[index](map); }
            finally { WindowedMapBridge.EndMapDraw(); }
        }
        static void OnFocus(IntPtr animation) { ScopedIdle(animation, originalFocus); }
        static void OnTransparent(IntPtr animation) { ScopedIdle(animation, originalTransparent); }
        static void ScopedIdle(IntPtr animation, Idle original)
        {
            bool mapOwned = active != IntPtr.Zero && Marshal.ReadIntPtr(animation, 4) == active;
            if (mapOwned) WindowedMapBridge.BeginMapDraw();
            try { original(animation); }
            finally { if (mapOwned) WindowedMapBridge.EndMapDraw(); }
        }
        static void OnMiniSize(IntPtr self, IntPtr value)
        {
            mini = self; UpdateMini(self);
        }
        static void UpdateMini(IntPtr self)
        {
            IntPtr value = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt32(value, width / 64 + 1); Marshal.WriteInt32(value, 4, height / 64 + 1);
                originalMini(self, value);
            }
            finally { Marshal.FreeHGlobal(value); }
        }
    }

    // Startup runs before the game creates windows or starts any map drawing.
    // Each mutation is recorded so installation can be rolled back as one unit.
    public static class NativePatch
    {
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool VirtualProtect(IntPtr p, UIntPtr n, uint flags, out uint old);
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr VirtualAlloc(IntPtr p, UIntPtr n, uint allocation, uint flags);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool FlushInstructionCache(IntPtr process, IntPtr p, UIntPtr n);
        static readonly Dictionary<int, byte[]> originals = new Dictionary<int, byte[]>();
        public static void Require(int address, byte[] expected)
        {
            byte[] actual = new byte[expected.Length]; Marshal.Copy((IntPtr)address, actual, 0, actual.Length);
            for (int i=0;i<actual.Length;i++) if (actual[i]!=expected[i])
                throw new InvalidOperationException("Native signature mismatch at 0x" + address.ToString("X8"));
        }
        public static void RequireCall(int address, int target)
        { Require(address, Branch(0xe8,address,target)); }
        public static byte[] Branch(byte opcode, int from, int to)
        { byte[] b=new byte[5]; b[0]=opcode; Buffer.BlockCopy(BitConverter.GetBytes(unchecked(to-from-5)),0,b,1,4); return b; }
        public static void Write(int address, byte[] value)
        {
            if (!originals.ContainsKey(address)) { byte[] oldBytes=new byte[value.Length]; Marshal.Copy((IntPtr)address,oldBytes,0,oldBytes.Length); originals.Add(address,oldBytes); }
            uint old;
            if (!VirtualProtect((IntPtr)address,(UIntPtr)value.Length,0x40,out old)) throw new System.ComponentModel.Win32Exception();
            try { Marshal.Copy(value,0,(IntPtr)address,value.Length); }
            finally { uint ignored; VirtualProtect((IntPtr)address,(UIntPtr)value.Length,old,out ignored); }
            if (!FlushInstructionCache(GetCurrentProcess(),(IntPtr)address,(UIntPtr)value.Length)) throw new System.ComponentModel.Win32Exception();
        }
        public static void Pointer(int address, Delegate callback)
        { Write(address,BitConverter.GetBytes(Marshal.GetFunctionPointerForDelegate(callback).ToInt32())); }
        public static IntPtr Trampoline(int address, byte[] displaced, Delegate callback)
        {
            Require(address,displaced);
            IntPtr code=VirtualAlloc(IntPtr.Zero,(UIntPtr)(displaced.Length+5),0x3000,0x40);
            if (code==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            Marshal.Copy(displaced,0,code,displaced.Length);
            byte[] tail=Branch(0xe9,code.ToInt32()+displaced.Length,address+displaced.Length);
            Marshal.Copy(tail,0,IntPtr.Add(code,displaced.Length),5);
            FlushInstructionCache(GetCurrentProcess(),code,(UIntPtr)(displaced.Length+5));
            byte[] redirect=new byte[displaced.Length]; for(int i=0;i<redirect.Length;i++)redirect[i]=0x90;
            Buffer.BlockCopy(Branch(0xe9,address,Marshal.GetFunctionPointerForDelegate(callback).ToInt32()),0,redirect,0,5);
            Write(address,redirect); return code;
        }
        public static void Rollback()
        {
            var copy=new List<KeyValuePair<int,byte[]>>(originals);
            copy.Reverse(); foreach(var item in copy) Write(item.Key,item.Value);
        }
    }
}
