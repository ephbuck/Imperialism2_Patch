using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;

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
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        delegate void Border(IntPtr self, int x, int y);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        delegate void ConvertEdgePoint(IntPtr self,IntPtr point);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void NativeInt(int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void CopyBits(IntPtr source, IntPtr destination, IntPtr sr, IntPtr dr, int mode, IntPtr mask);

        static readonly Draw drawHook = OnDraw;
        static readonly CopyBits copyHook = OnMapCopy;
        static readonly Idle focusHook = OnFocus, transparentHook = OnTransparent;
        static readonly MiniSize miniHook = OnMiniSize;
        static readonly Border borderHook=OnBorder;
        static readonly ConvertEdgePoint edgePointHook=OnEdgePoint;
        static ConvertEdgePoint originalEdgePoint;
        static IntPtr edgePointTarget;
        static readonly Dictionary<int, Selection> selections = new Dictionary<int, Selection>();
        static readonly List<Delegate> roots = new List<Delegate>();
        static Draw originalDraw;
        static Idle originalFocus, originalTransparent;
        static MiniSize originalMini;
        static CopyBits originalCopy;
        static Border originalBorder;
        static NativeInt borderColor, foreColor;
        static IntPtr active, mini;
        static IntPtr rectangle = Marshal.AllocHGlobal(16);
        static bool installed, drawing, sizeDirty = true;
        static bool restoringTerrain;
        static long terrainRestores,terrainRestoreMisses;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void ProjectTile(int tile,IntPtr camera,IntPtr y,IntPtr x,int divisor);
        static ProjectTile project;
        static int width = 576, height = 512;
        public static int MapWidth { get { return width; } }
        public static int MapHeight { get { return height; } }
        public static IntPtr ActiveMap { get { return active; } }
        public static long Frames { get; private set; }
        static readonly Queue<double> drawTimes = new Queue<double>();
        public static string Diagnostics()
        {
            double[] times=drawTimes.ToArray(); Array.Sort(times);
            return String.Format(CultureInfo.InvariantCulture,"frames={0}, recentDrawSamples={1}, drawP50Ms={2:F3}, drawP95Ms={3:F3}, camera=({4},{5}), terrainRestores={6}, terrainRestoreMisses={7}, terrainRestoreErrors={8}",
                Frames,times.Length,times.Length==0?0:times[(times.Length-1)/2],times.Length==0?0:times[(int)((times.Length-1)*.95)],
                active==IntPtr.Zero?0:Marshal.ReadInt32(active,0x64),active==IntPtr.Zero?0:Marshal.ReadInt32(active,0x68),terrainRestores,terrainRestoreMisses,Imp2Net.Graphics.NativeMapCache.RegenerationErrors);
        }

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
            NativePatch.RequireCall(0x55d800, 0x4028a1);
            NativePatch.Require(0x4b6ce8,new byte[]{0xff,0x90,0x48,1,0,0});
            RequireGeometry();
            // A maximum-sized guarded tactical target is allocated once by the
            // original engine and reused across resize. No live GWorld is freed.
            NativePatch.Write(0x532eac, new byte[] { 0xba,0xc0,0xff,0xff,0xff,0x89,0x54,0x24,0,
                0x89,0x54,0x24,4,0xc7,0x44,0x24,8,0,0x0f,0,0,0xc7,0x44,0x24,0x0c,0xb0,8,0,0,0x90 });
            // Ask DirectDraw for DDSCL_NORMAL; never switch the desktop mode.
            NativePatch.Write(0x49435a, new byte[] { 0x31,0xc9,0x90 });
            originalDraw = (Draw)Marshal.GetDelegateForFunctionPointer((IntPtr)0x409ade, typeof(Draw));
            originalCopy = (CopyBits)Marshal.GetDelegateForFunctionPointer((IntPtr)0x404e8f, typeof(CopyBits));
            project=(ProjectTile)Marshal.GetDelegateForFunctionPointer((IntPtr)0x4050dd,typeof(ProjectTile));
            originalBorder=(Border)Marshal.GetDelegateForFunctionPointer((IntPtr)0x4028a1,typeof(Border));
            borderColor=(NativeInt)Marshal.GetDelegateForFunctionPointer((IntPtr)0x4010f5,typeof(NativeInt));
            foreColor=(NativeInt)Marshal.GetDelegateForFunctionPointer((IntPtr)0x4067f8,typeof(NativeInt));
            foreach (int slot in new int[] { 0x703a14,0x703ca4,0x703f34 }) NativePatch.Pointer(slot, drawHook);
            NativePatch.Write(0x55d889, NativePatch.Branch(0xe8, 0x55d889, Marshal.GetFunctionPointerForDelegate(copyHook).ToInt32()));
            NativePatch.Write(0x55d800,NativePatch.Branch(0xe8,0x55d800,Marshal.GetFunctionPointerForDelegate(borderHook).ToInt32()));
            byte[] edgeCall=new byte[6];
            Array.Copy(NativePatch.Branch(0xe8,0x4b6ce8,Marshal.GetFunctionPointerForDelegate(edgePointHook).ToInt32()),edgeCall,5);
            edgeCall[5]=0x90;NativePatch.Write(0x4b6ce8,edgeCall);
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
            Imp2Net.Graphics.NativeMapCache.EnsureTerrain=EnsureTerrain;
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
            Imp2Net.Graphics.PixelRect bounds = WindowedMapBridge.Layout.Map;
            width = bounds.Width; height = bounds.Height;
            NativeMapInput.SetMapBounds(bounds.Left,bounds.Top,width,height);
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

        // The root still dispatches the logical 800x600 UI. Leaving the map
        // child enlarged between draws makes it cover the logical sidebar and
        // steal its clicks. Native map rendering alone needs the larger extent.
        internal static IDisposable UseNativeMapBounds(IntPtr map)
        { return new MapBoundsScope(map,width,height); }
        sealed class MapBoundsScope : IDisposable
        {
            readonly IntPtr map;
            readonly int oldWidth,oldHeight;
            bool disposed;
            internal MapBoundsScope(IntPtr map,int w,int h)
            {
                this.map=map;oldWidth=Marshal.ReadInt32(map,0x34);oldHeight=Marshal.ReadInt32(map,0x38);
                Marshal.WriteInt32(map,0x34,w);Marshal.WriteInt32(map,0x38,h);
            }
            public void Dispose() {
                if(disposed)return;disposed=true;
                Marshal.WriteInt32(map,0x34,oldWidth);Marshal.WriteInt32(map,0x38,oldHeight);
            }
        }

        static int EnsureTerrain(IntPtr map,int tile)
        {
            if(restoringTerrain || drawing || map!=active || tile<0 || tile>=6480) return -1;
            IntPtr scratch=Marshal.AllocHGlobal(24);
            restoringTerrain=true;
            try
            {
                ProjectForTerrain(map,tile,scratch);
                int x=Marshal.ReadInt16(scratch),y=Marshal.ReadInt16(scratch,2);
                // A native tile draw needs neighboring coverage. This bounded
                // 128px area is comfortably below the 110-entry cache capacity.
                Rect region=new Rect { Left=Math.Max(0,x-32),Top=Math.Max(0,y),Right=Math.Min(width,x+96),Bottom=Math.Min(height,y+128) };
                if(region.Right<=region.Left || region.Bottom<=region.Top) return -1;
                Marshal.StructureToPtr(region,IntPtr.Add(scratch,8),false);
                WindowedMapBridge.BeginMapDraw();
                try { using(UseNativeMapBounds(map)) originalDraw(map,IntPtr.Add(scratch,8)); }
                finally { WindowedMapBridge.EndMapDraw(); }
                IntPtr world=Marshal.ReadIntPtr((IntPtr)0x7606f4),tiles=Marshal.ReadIntPtr(world,0x10);
                int slot=Marshal.ReadByte(tiles,tile*40+0x13);
                if(slot<110 && Marshal.ReadInt16(map,0x8a+slot*8)==tile) { terrainRestores++;return slot; }
                terrainRestoreMisses++;return -1;
            }
            finally { restoringTerrain=false;Marshal.FreeHGlobal(scratch); }
        }
        internal static void ProjectForTerrain(IntPtr map,int tile,IntPtr xy)
        {
            // Native 0x54F680 writes vertical output first, horizontal second.
            project(tile,IntPtr.Add(map,0x64),IntPtr.Add(xy,2),xy,1);
        }

        static void OnDraw(IntPtr map, IntPtr dirty)
        {
            if (drawing) { originalDraw(map, dirty); return; }
            active = map;
            NativeMapInput.SetActiveMap(map);
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
            long start=Stopwatch.GetTimestamp();
            WindowedMapBridge.BeginMapDraw();
            try { using(UseNativeMapBounds(map)) originalDraw(map, rectangle); Frames++;WindowedMapBridge.MapFrameReady(); }
            finally { WindowedMapBridge.EndMapDraw(); drawing = false;
                drawTimes.Enqueue((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency);
                if(drawTimes.Count>600) drawTimes.Dequeue(); }
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

        // REA map-frame-body: 0x60A240 copies two masked 58x512 frame
        // strips from world+0x314. Its right offset is permanently 518.
        // Only replace the map Draw call; unrelated frame consumers retain it.
        static void OnBorder(IntPtr self,int x,int y)
        {
            if(width==576 && height==512) { originalBorder(self,x,y); return; }
            IntPtr resources=Marshal.ReadIntPtr((IntPtr)0x75a650);
            IntPtr source=IntPtr.Add(Marshal.ReadIntPtr(resources,0x314),4);
            IntPtr destination=IntPtr.Add(Marshal.ReadIntPtr((IntPtr)0x759f90),4);
            IntPtr rects=Marshal.AllocHGlobal(32);
            borderColor(253); foreColor(0);
            try
            {
                for(int side=0;side<2;side++)
                {
                    int sx=side*58,dx=x+(side==0?0:width-58);
                    BorderPiece(source,destination,rects,sx,0,dx,y,58);
                    for(int offset=58;offset<height-64;offset+=64)
                        BorderPiece(source,destination,rects,sx,128,dx,y+offset,Math.Min(64,height-64-offset));
                    BorderPiece(source,destination,rects,sx,448,dx,y+height-64,64);
                }
            }
            finally { borderColor(255); Marshal.FreeHGlobal(rects); }
        }
        static void BorderPiece(IntPtr source,IntPtr destination,IntPtr rects,int sx,int sy,int dx,int dy,int h)
        {
            Marshal.StructureToPtr(new Rect{Left=sx,Top=sy,Right=sx+58,Bottom=sy+h},rects,false);
            Marshal.StructureToPtr(new Rect{Left=dx,Top=dy,Right=dx+58,Bottom=dy+h},IntPtr.Add(rects,16),false);
            originalCopy(source,destination,rects,IntPtr.Add(rects,16),36,IntPtr.Zero);
        }

        static void OnSelection(IntPtr map, int index)
        {
            WindowedMapBridge.BeginMapDraw();
            try { using(UseNativeMapBounds(map)) selections[index](map); }
            finally { WindowedMapBridge.EndMapDraw(); }
        }
        // Native root autoscroll (0x4B6C60) first checks modal/focus state,
        // converts a temporary point, tests a 4px boundary, then throttles and
        // invokes the original directional scroll. Adapt only that temporary
        // point; keep the native guards, direction flags and camera behavior.
        static void OnEdgePoint(IntPtr view,IntPtr point)
        {
            IntPtr target=Marshal.ReadIntPtr(Marshal.ReadIntPtr(view),0x148);
            if(target!=edgePointTarget) {
                originalEdgePoint=(ConvertEdgePoint)Marshal.GetDelegateForFunctionPointer(target,typeof(ConvertEdgePoint));
                edgePointTarget=target;
            }
            originalEdgePoint(view,point);
            if(!IsActive() || NativeMapInput.PhysicalCursor==null) return;
            NativeMapInput.Point cursor=NativeMapInput.PhysicalCursor();
            NativeMapInput.Point edge=NativeMapInput.EdgeScrollPoint(cursor.X,cursor.Y,
                Marshal.ReadInt32(view,0x34),Marshal.ReadInt32(view,0x38));
            Marshal.StructureToPtr(edge,point,false);
        }
        static void OnFocus(IntPtr animation) { ScopedIdle(animation, originalFocus); }
        static void OnTransparent(IntPtr animation) { ScopedIdle(animation, originalTransparent); }
        static void ScopedIdle(IntPtr animation, Idle original)
        {
            bool mapOwned = active != IntPtr.Zero && Marshal.ReadIntPtr(animation, 4) == active;
            if (mapOwned) WindowedMapBridge.BeginMapDraw();
            try { if(mapOwned) { using(UseNativeMapBounds(active)) original(animation); } else original(animation); }
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
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool VirtualFree(IntPtr p,UIntPtr n,uint kind);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool FlushInstructionCache(IntPtr process, IntPtr p, UIntPtr n);
        static readonly Dictionary<int, byte[]> originals = new Dictionary<int, byte[]>();
        static readonly List<IntPtr> trampolines=new List<IntPtr>();
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
            finally { uint ignored; if(!VirtualProtect((IntPtr)address,(UIntPtr)value.Length,old,out ignored)) throw new System.ComponentModel.Win32Exception(); }
            if (!FlushInstructionCache(GetCurrentProcess(),(IntPtr)address,(UIntPtr)value.Length)) throw new System.ComponentModel.Win32Exception();
        }
        public static void Pointer(int address, Delegate callback)
        { Write(address,BitConverter.GetBytes(Marshal.GetFunctionPointerForDelegate(callback).ToInt32())); }
        public static IntPtr Trampoline(int address, byte[] displaced, Delegate callback)
        {
            Require(address,displaced);
            IntPtr code=VirtualAlloc(IntPtr.Zero,(UIntPtr)(displaced.Length+5),0x3000,0x04);
            if (code==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            trampolines.Add(code);
            Marshal.Copy(displaced,0,code,displaced.Length);
            byte[] tail=Branch(0xe9,code.ToInt32()+displaced.Length,address+displaced.Length);
            Marshal.Copy(tail,0,IntPtr.Add(code,displaced.Length),5);
            uint old;
            if(!VirtualProtect(code,(UIntPtr)(displaced.Length+5),0x20,out old) || !FlushInstructionCache(GetCurrentProcess(),code,(UIntPtr)(displaced.Length+5)))
                throw new System.ComponentModel.Win32Exception();
            byte[] redirect=new byte[displaced.Length]; for(int i=0;i<redirect.Length;i++)redirect[i]=0x90;
            Buffer.BlockCopy(Branch(0xe9,address,Marshal.GetFunctionPointerForDelegate(callback).ToInt32()),0,redirect,0,5);
            Write(address,redirect); return code;
        }
        public static void Rollback()
        {
            var copy=new List<KeyValuePair<int,byte[]>>(originals);
            copy.Reverse(); foreach(var item in copy) Write(item.Key,item.Value);
            originals.Clear();
            foreach(IntPtr p in trampolines) if(!VirtualFree(p,UIntPtr.Zero,0x8000)) throw new System.ComponentModel.Win32Exception();
            trampolines.Clear();
        }
    }
}
