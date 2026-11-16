using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Imp2Net
{
    // Presentation and Win32 window management only. NativeMapRenderer owns map
    // geometry, cache lifetime, native dispatch and the semantic drawing scopes.
    public static class WindowedMapBridge
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct WindowPosition { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo { public Point Reserved, MaximumSize, MaximumPosition, MinimumTrack, MaximumTrack; }

        const uint Srccopy = 0x00CC0020, WindowStyle = 0x00CF0000, Visible = 0x10000000;
        const int GwlStyle = -16, GwlWndProc = -4;
        const uint NoSize = 1, NoMove = 2, NoZOrder = 4, FrameChanged = 0x20;
        static IntPtr gameWindow, viewWindow, gameProcedure, viewProcedure;
        static IntPtr uiDc, uiBitmap, uiOldBitmap, mapDc, mapBitmap, mapOldBitmap;
        static bool initialized, resizing, presenting, mapNeedsRedraw;
        [ThreadStatic] static int mapDepth;
        static int requestedWidth = 1280, requestedHeight = 900;
        static readonly List<Delegate> pinned = new List<Delegate>();
        static readonly List<KeyValuePair<IntPtr, IntPtr>> installed = new List<KeyValuePair<IntPtr, IntPtr>>();
        public static int ClientWidth { get; private set; }
        public static int ClientHeight { get; private set; }
        public static IntPtr ViewWindow { get { return viewWindow; } }
        public static bool IsEnabled { get { return initialized; } }
        public static bool InsideMapDraw { get { return mapDepth > 0; } }
        public static Func<bool> MapActive;
        public static Action<int, int> SizeChanged;
        public static Action RenderMap;
        // The callback receives physical, unscaled coordinates. Native map input
        // must not be clamped to the legacy 800x600 root hit-test rectangle.
        public static Func<uint, IntPtr, int, int, bool> MapMouseMessage;
        public static Func<int, int, Point?> MapCursorToProxy;
        static WindowedMapBridge() { ClientWidth = 800; ClientHeight = 600; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr CreateDelegate(uint exStyle, IntPtr cls, IntPtr title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool PositionDelegate(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool MoveDelegate(IntPtr window, int x, int y, int w, int h, bool repaint);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr DeferDelegate(IntPtr info, IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool BlitDelegate(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint rop);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DibDelegate(IntPtr dest, int x, int y, int w, int h, int sx, int sy, int sw, int sh, IntPtr bits, IntPtr info, uint usage, uint rop);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr ProcedureDelegate(IntPtr window, uint message, IntPtr wp, IntPtr lp);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool ConvertPointDelegate(IntPtr window, ref Point point);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool ClientRectDelegate(IntPtr window, out Rect rect);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int RectDrawDelegate(IntPtr dc, IntPtr rect, IntPtr brush);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool RectangleDelegate(IntPtr dc, int left, int top, int right, int bottom);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool PolylineDelegate(IntPtr dc, IntPtr points, int count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool LineDelegate(IntPtr dc, int x, int y);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool MoveToDelegate(IntPtr dc, int x, int y, IntPtr old);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr LongDelegate(IntPtr window, int index, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool ShowDelegate(IntPtr window, int command);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool PlacementDelegate(IntPtr window, IntPtr placement);

        static readonly ProcedureDelegate gameHook = GameProcedure, viewHook = ViewProcedure;
        [DllImport("ole32.dll")] static extern int CoInitialize(IntPtr reserved);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetModuleHandleA(string module);
        [DllImport("kernel32.dll")] static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint oldProtection);
        [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern IntPtr CreateWindowExA(uint exStyle, IntPtr cls, IntPtr title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr window, int x, int y, int w, int h, bool repaint);
        [DllImport("user32.dll")] static extern IntPtr DeferWindowPos(IntPtr info, IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLongA(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll")] static extern IntPtr GetWindowLongA(IntPtr window, int index);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr window, IntPtr placement);
        [DllImport("user32.dll")] static extern IntPtr CallWindowProcA(IntPtr procedure, IntPtr window, uint message, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcA(IntPtr window, uint message, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] static extern bool AdjustWindowRectEx(ref Rect rect, uint style, bool menu, uint exStyle);
        [DllImport("user32.dll")] static extern bool SystemParametersInfoA(uint action, uint param, out Rect rect, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr WindowFromDC(IntPtr dc);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window, ref Point point);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, IntPtr rect, IntPtr brush);
        [DllImport("user32.dll")] static extern int FrameRect(IntPtr dc, IntPtr rect, IntPtr brush);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern int StretchDIBits(IntPtr dest, int x, int y, int w, int h, int sx, int sy, int sw, int sh, IntPtr bits, IntPtr info, uint usage, uint rop);
        [DllImport("gdi32.dll")] static extern bool Rectangle(IntPtr dc, int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] static extern bool Polyline(IntPtr dc, IntPtr points, int count);
        [DllImport("gdi32.dll")] static extern bool LineTo(IntPtr dc, int x, int y);
        [DllImport("gdi32.dll")] static extern bool MoveToEx(IntPtr dc, int x, int y, IntPtr previous);
        [DllImport("gdi32.dll")] static extern int SaveDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool RestoreDC(IntPtr dc, int saved);
        [DllImport("gdi32.dll")] static extern int SelectClipRgn(IntPtr dc, IntPtr region);
        [DllImport("gdi32.dll")] static extern IntPtr GetCurrentObject(IntPtr dc, uint type);
        [DllImport("gdi32.dll")] static extern bool PatBlt(IntPtr dc, int x, int y, int width, int height, uint rop);

        public static int Initialize(IntPtr reserved)
        {
            int result = CoInitialize(reserved);
            if (initialized) return result;
            if (IntPtr.Size != 4 || GetModuleHandleA(null).ToInt32() != 0x400000) return result;
            Validate();
            ReadConfiguration();
            try
            {
                InstallHooks();
                initialized = true;
            }
            catch
            {
                foreach (KeyValuePair<IntPtr, IntPtr> item in installed) WritePointer(item.Key, item.Value);
                installed.Clear();
                throw;
            }
            return result;
        }
        public static void Validate()
        {
            if (IntPtr.Size != 4 || GetModuleHandleA(null).ToInt32() != 0x400000)
                throw new InvalidOperationException("Expected the 32-bit Imperialism II image at 0x400000.");
            // Current retail 1.03 map entry, independent of startup-thunk changes.
            byte[] signature = { 0x64, 0xa1, 0, 0, 0, 0, 0x6a, 0xff };
            for (int i = 0; i < signature.Length; ++i)
                if (Marshal.ReadByte(new IntPtr(0x55d070), i) != signature[i])
                    throw new InvalidOperationException("Unsupported map renderer signature.");
            foreach (string name in new string[] { "CreateWindowExA", "SetWindowPos", "MoveWindow", "DeferWindowPos", "GetClientRect", "ScreenToClient", "ClientToScreen", "FillRect", "FrameRect", "SetWindowLongA", "ShowWindow", "SetWindowPlacement" }) ImportSlot("USER32.dll", name);
            foreach (string name in new string[] { "BitBlt", "StretchDIBits", "Rectangle", "Polyline", "LineTo", "MoveToEx" }) ImportSlot("GDI32.dll", name);
        }
        static void InstallHooks()
        {
                Install("USER32.dll", "CreateWindowExA", new CreateDelegate(CreateWindow));
                Install("USER32.dll", "SetWindowPos", new PositionDelegate(PositionWindow));
                Install("USER32.dll", "MoveWindow", new MoveDelegate(MoveGameWindow));
                Install("USER32.dll", "DeferWindowPos", new DeferDelegate(DeferGameWindow));
                Install("USER32.dll", "SetWindowLongA", new LongDelegate(SetGameLong));
                Install("USER32.dll", "ShowWindow", new ShowDelegate(ShowGameWindow));
                Install("USER32.dll", "SetWindowPlacement", new PlacementDelegate(PlaceGameWindow));
                Install("USER32.dll", "GetClientRect", new ClientRectDelegate(LogicalClientRect));
                Install("USER32.dll", "ScreenToClient", new ConvertPointDelegate(ToLogicalClient));
                Install("USER32.dll", "ClientToScreen", new ConvertPointDelegate(FromLogicalClient));
                Install("GDI32.dll", "BitBlt", new BlitDelegate(CaptureBitBlt));
                Install("GDI32.dll", "StretchDIBits", new DibDelegate(CaptureDib));
                Install("USER32.dll", "FillRect", new RectDrawDelegate(CaptureFill));
                Install("USER32.dll", "FrameRect", new RectDrawDelegate(CaptureFrame));
                Install("GDI32.dll", "Rectangle", new RectangleDelegate(CaptureRectangle));
                Install("GDI32.dll", "Polyline", new PolylineDelegate(CapturePolyline));
                Install("GDI32.dll", "LineTo", new LineDelegate(CaptureLine));
                Install("GDI32.dll", "MoveToEx", new MoveToDelegate(CaptureMove));
        }

        static void ReadConfiguration()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dplayx.ini");
            if (!File.Exists(path)) return;
            bool graphics = false;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("[")) { graphics = line.Equals("[Graphics]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!graphics || line.StartsWith(";")) continue;
                string[] pair = line.Split(new[] { '=' }, 2); int value;
                if (pair.Length != 2 || !Int32.TryParse(pair[1].Trim(), out value)) continue;
                if (pair[0].Trim().Equals("WindowWidth", StringComparison.OrdinalIgnoreCase)) requestedWidth = Math.Max(800, Math.Min(3840, value));
                if (pair[0].Trim().Equals("WindowHeight", StringComparison.OrdinalIgnoreCase)) requestedHeight = Math.Max(600, Math.Min(2160, value));
            }
        }

        // Find by PE import name, rather than trusting an address from another
        // linked executable. The game entry signature above gates this profile.
        static IntPtr ImportSlot(string library, string function)
        {
            IntPtr image = GetModuleHandleA(null);
            int pe = Marshal.ReadInt32(image, 0x3c);
            if (Marshal.ReadInt32(image, pe) != 0x4550 || Marshal.ReadInt16(image, pe + 4) != 0x14c)
                throw new InvalidOperationException("Unsupported executable headers.");
            int directory = Marshal.ReadInt32(image, pe + 0x80);
            for (int descriptor = directory; Marshal.ReadInt32(image, descriptor + 12) != 0; descriptor += 20)
            {
                string name = Marshal.PtrToStringAnsi(IntPtr.Add(image, Marshal.ReadInt32(image, descriptor + 12)));
                if (!name.Equals(library, StringComparison.OrdinalIgnoreCase)) continue;
                int names = Marshal.ReadInt32(image, descriptor), slots = Marshal.ReadInt32(image, descriptor + 16);
                if (names == 0) throw new InvalidOperationException("Import names unavailable.");
                for (int index = 0; ; index += 4)
                {
                    uint entry = unchecked((uint)Marshal.ReadInt32(image, names + index));
                    if (entry == 0) break;
                    if ((entry & 0x80000000) != 0) continue;
                    if (Marshal.PtrToStringAnsi(IntPtr.Add(image, checked((int)entry + 2))) == function)
                        return IntPtr.Add(image, slots + index);
                }
            }
            throw new InvalidOperationException("Required game import not found: " + library + "!" + function);
        }
        static void WritePointer(IntPtr address, IntPtr value)
        {
            uint protection, ignored;
            if (!VirtualProtect(address, (UIntPtr)4, 0x40, out protection)) throw new System.ComponentModel.Win32Exception();
            Marshal.WriteIntPtr(address, value);
            VirtualProtect(address, (UIntPtr)4, protection, out ignored);
        }
        static void Install(string library, string name, Delegate callback)
        {
            IntPtr slot = ImportSlot(library, name), previous = Marshal.ReadIntPtr(slot);
            pinned.Add(callback);
            installed.Add(new KeyValuePair<IntPtr, IntPtr>(slot, previous));
            WritePointer(slot, Marshal.GetFunctionPointerForDelegate(callback));
        }
        static string ReadName(IntPtr value) { return unchecked((uint)value.ToInt32()) > 0xffff ? Marshal.PtrToStringAnsi(value) : String.Empty; }
        static bool Active { get { return MapActive != null && MapActive(); } }
        static bool IsScreen(IntPtr dc)
        {
            if (dc == IntPtr.Zero) return false;
            IntPtr window = WindowFromDC(dc);
            return window != IntPtr.Zero && (window == viewWindow || window == gameWindow);
        }

        static void ComputeCenteredFrame(uint style, bool hasMenu, uint ex, out int x, out int y, out int outerW, out int outerH)
        {
            Rect workArea;
            if (!SystemParametersInfoA(48, 0, out workArea, 0) || workArea.Right <= workArea.Left || workArea.Bottom <= workArea.Top)
                workArea = new Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            Rect frame = new Rect { Right = requestedWidth, Bottom = requestedHeight };
            AdjustWindowRectEx(ref frame, style, hasMenu, ex);
            int workW = workArea.Right - workArea.Left, workH = workArea.Bottom - workArea.Top;
            outerW = Math.Min(frame.Right - frame.Left, workW);
            outerH = Math.Min(frame.Bottom - frame.Top, workH);
            x = workArea.Left + Math.Max(0, (workW - outerW) / 2);
            y = workArea.Top + Math.Max(0, (workH - outerH) / 2);
        }

        static IntPtr CreateWindow(uint ex, IntPtr cls, IntPtr title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param)
        {
            string className = ReadName(cls), caption = ReadName(title);
            bool view = className.IndexOf("AmbitGameWindow", StringComparison.OrdinalIgnoreCase) >= 0;
            bool game = parent == IntPtr.Zero && (view || caption.IndexOf("Imperialism", StringComparison.OrdinalIgnoreCase) >= 0);
            if (game)
            {
                style = WindowStyle | Visible | 0x02000000;
                ComputeCenteredFrame(style, menu != IntPtr.Zero, ex, out x, out y, out w, out h);
            }
            if (view && parent != IntPtr.Zero)
            {
                Rect client; GetClientRect(parent, out client);
                x = y = 0; w = Math.Max(800, client.Right); h = Math.Max(600, client.Bottom);
                style |= 0x06000000;
            }
            IntPtr window = CreateWindowExA(ex, cls, title, style, x, y, w, h, parent, menu, instance, param);
            if (window == IntPtr.Zero) return window;
            if (game) { gameWindow = window; ConfigureGame(); }
            if (view)
            {
                viewWindow = window;
                if (gameWindow == IntPtr.Zero) { gameWindow = parent; ConfigureGame(); }
                viewProcedure = SetWindowLongA(window, GwlWndProc, Marshal.GetFunctionPointerForDelegate(viewHook));
                ResizeFromWindow();
            }
            return window;
        }
        static void ConfigureGame()
        {
            if (gameWindow == IntPtr.Zero || gameProcedure != IntPtr.Zero) return;
            SetWindowLongA(gameWindow, GwlStyle, new IntPtr(unchecked((int)(WindowStyle | Visible | 0x02000000))));
            gameProcedure = SetWindowLongA(gameWindow, GwlWndProc, Marshal.GetFunctionPointerForDelegate(gameHook));
            int x, y, outerW, outerH;
            ComputeCenteredFrame(WindowStyle, false, 0, out x, out y, out outerW, out outerH);
            SetWindowPos(gameWindow, IntPtr.Zero, x, y, outerW, outerH, NoZOrder | FrameChanged | 0x40);
        }
        static bool PositionWindow(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags)
        {
            if (window == gameWindow) flags |= NoSize | NoMove | NoZOrder;
            if (window == viewWindow && window != gameWindow) { x = y = 0; w = ClientWidth; h = ClientHeight; flags &= ~(NoSize | NoMove); after = IntPtr.Zero; flags |= NoZOrder; }
            return SetWindowPos(window, after, x, y, w, h, flags);
        }
        static IntPtr SetGameLong(IntPtr window, int index, IntPtr value)
        {
            if (window == gameWindow && index == GwlStyle)
                value = new IntPtr(unchecked((int)(WindowStyle | Visible | 0x02000000)));
            // Preserve the bridge subclass when the engine replaces its native
            // WndProc after CreateWindowEx has returned.
            if (index == GwlWndProc && window == gameWindow && gameProcedure != IntPtr.Zero)
            { IntPtr old = gameProcedure; gameProcedure = value; return old; }
            if (index == GwlWndProc && window == viewWindow && viewProcedure != IntPtr.Zero)
            { IntPtr old = viewProcedure; viewProcedure = value; return old; }
            return SetWindowLongA(window, index, value);
        }
        static bool ShowGameWindow(IntPtr window, int command)
        {
            // Calls imported by the engine must not force its old fullscreen
            // startup. The title-bar maximize button uses the OS window manager
            // directly, so user maximize/minimize/restore continue to work.
            if (window == gameWindow && (command == 3 || command == 10)) command = 1;
            return ShowWindow(window, command);
        }
        static bool PlaceGameWindow(IntPtr window, IntPtr placement)
        {
            // The engine's saved placement is an 800x600 fullscreen rectangle.
            // Resizing and user window state are owned by the OS in this mode.
            if (window == gameWindow) return true;
            return SetWindowPlacement(window, placement);
        }
        static bool MoveGameWindow(IntPtr window, int x, int y, int w, int h, bool repaint)
        {
            if (window == gameWindow) return true;
            if (window == viewWindow && window != gameWindow) { x = y = 0; w = ClientWidth; h = ClientHeight; }
            return MoveWindow(window, x, y, w, h, repaint);
        }
        static IntPtr DeferGameWindow(IntPtr info, IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags)
        {
            if (window == gameWindow) flags |= NoSize | NoMove | NoZOrder;
            if (window == viewWindow && window != gameWindow) { x = y = 0; w = ClientWidth; h = ClientHeight; flags &= ~(NoSize | NoMove); flags |= NoZOrder; }
            return DeferWindowPos(info, window, after, x, y, w, h, flags);
        }
        static void ResizeFromWindow()
        {
            if (resizing || gameWindow == IntPtr.Zero) return;
            Rect client;
            if (!GetClientRect(gameWindow, out client) || client.Right < 800 || client.Bottom < 600) return;
            int width = Math.Min(3840, client.Right), height = Math.Min(2160, client.Bottom);
            if (width == ClientWidth && height == ClientHeight && mapDc != IntPtr.Zero) return;
            resizing = true;
            try
            {
                ReplaceMapBacking(width, height);
                ClientWidth = width; ClientHeight = height;
                mapNeedsRedraw = true;
                if (viewWindow != IntPtr.Zero && viewWindow != gameWindow) SetWindowPos(viewWindow, IntPtr.Zero, 0, 0, width, height, NoZOrder);
                if (SizeChanged != null) SizeChanged(width, height);
                if (viewWindow != IntPtr.Zero) InvalidateRect(viewWindow, IntPtr.Zero, false);
            }
            finally { resizing = false; }
        }

        static void MakeBacking(IntPtr reference, int width, int height, out IntPtr dc, out IntPtr bitmap, out IntPtr previous)
        {
            dc = CreateCompatibleDC(reference); bitmap = CreateCompatibleBitmap(reference, width, height); previous = IntPtr.Zero;
            if (dc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (dc != IntPtr.Zero) DeleteDC(dc);
                throw new InvalidOperationException("Cannot allocate window presentation backing.");
            }
            previous = SelectObject(dc, bitmap);
            PatBlt(dc, 0, 0, width, height, 0x42); // BLACKNESS
        }
        static void ReleaseBacking(ref IntPtr dc, ref IntPtr bitmap, ref IntPtr previous)
        {
            if (dc != IntPtr.Zero && previous != IntPtr.Zero) SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            dc = bitmap = previous = IntPtr.Zero;
        }
        static void EnsureBacking()
        {
            if (mapDc != IntPtr.Zero && uiDc != IntPtr.Zero) return;
            ReplaceMapBacking(ClientWidth, ClientHeight);
        }
        static void ReplaceMapBacking(int width, int height)
        {
            IntPtr reference = GetDC(viewWindow != IntPtr.Zero ? viewWindow : gameWindow);
            if (reference == IntPtr.Zero) return;
            try
            {
                IntPtr dc, bitmap, previous;
                MakeBacking(reference, width, height, out dc, out bitmap, out previous);
                try
                {
                    if (uiDc == IntPtr.Zero) MakeBacking(reference, 800, 600, out uiDc, out uiBitmap, out uiOldBitmap);
                }
                catch { ReleaseBacking(ref dc, ref bitmap, ref previous); throw; }
                ReleaseBacking(ref mapDc, ref mapBitmap, ref mapOldBitmap);
                mapDc = dc; mapBitmap = bitmap; mapOldBitmap = previous;
            }
            finally { ReleaseDC(viewWindow != IntPtr.Zero ? viewWindow : gameWindow, reference); }
        }
        static IntPtr Target(IntPtr dc)
        {
            if (!IsScreen(dc)) return dc;
            EnsureBacking();
            return mapDepth > 0 ? mapDc : uiDc;
        }
        public static void BeginMapDraw() { ++mapDepth; }
        public static void EndMapDraw() { if (mapDepth > 0) --mapDepth; if (mapDepth == 0) Present(); }
        public static void PresentNativeMap(IntPtr sourceDc, int sx, int sy, int width, int height, int x, int y)
        {
            EnsureBacking();
            if (mapDc != IntPtr.Zero) BitBlt(mapDc, x, y, width, height, sourceDc, sx, sy, Srccopy);
            if (mapDepth == 0) Present();
        }
        public static void PresentNativeMapDib(IntPtr bits, IntPtr bitmapInfo, int sx, int sy, int width, int height, int x, int y)
        {
            EnsureBacking();
            int bitmapHeight = Marshal.ReadInt32(bitmapInfo, 8);
            int sourceY = bitmapHeight > 0 ? bitmapHeight - sy - height : sy;
            if (mapDc != IntPtr.Zero) StretchDIBits(mapDc, x, y, width, height, sx, sourceY, width, height, bits, bitmapInfo, 0, Srccopy);
            if (mapDepth == 0) Present();
        }
        static bool CaptureBitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint rop)
        {
            bool screen = IsScreen(dest);
            bool result = BitBlt(Target(dest), x, y, w, h, Target(source), sx, sy, rop);
            if (screen && mapDepth == 0) Present();
            return result;
        }
        static int CaptureDib(IntPtr dest, int x, int y, int w, int h, int sx, int sy, int sw, int sh, IntPtr bits, IntPtr info, uint usage, uint rop)
        {
            bool screen = IsScreen(dest);
            int result = StretchDIBits(Target(dest), x, y, w, h, sx, sy, sw, sh, bits, info, usage, rop);
            if (screen && mapDepth == 0) Present();
            return result;
        }
        // Primitive screen draws are uncommon in the QuickDraw engine. Copy the
        // selected pen/brush/font when retargeting so memory DC defaults do not
        // change the native raster result.
        static int PreparePrimitive(IntPtr original, out IntPtr target)
        {
            target = Target(original);
            if (target == original) return 0;
            int saved = SaveDC(target);
            foreach (uint type in new uint[] { 1, 2, 6 }) { IntPtr value = GetCurrentObject(original, type); if (value != IntPtr.Zero) SelectObject(target, value); }
            return saved;
        }
        static void FinishPrimitive(IntPtr original, IntPtr target, int saved)
        {
            if (saved != 0) RestoreDC(target, saved);
            if (target != original && mapDepth == 0) Present();
        }
        static int CaptureFill(IntPtr dc, IntPtr rect, IntPtr brush) { int r = FillRect(Target(dc), rect, brush); if (IsScreen(dc) && mapDepth == 0) Present(); return r; }
        static int CaptureFrame(IntPtr dc, IntPtr rect, IntPtr brush) { int r = FrameRect(Target(dc), rect, brush); if (IsScreen(dc) && mapDepth == 0) Present(); return r; }
        static bool CaptureRectangle(IntPtr dc, int l, int t, int r, int b) { IntPtr target; int saved = PreparePrimitive(dc, out target); bool result = Rectangle(target, l, t, r, b); FinishPrimitive(dc, target, saved); return result; }
        static bool CapturePolyline(IntPtr dc, IntPtr points, int count) { IntPtr target; int saved = PreparePrimitive(dc, out target); bool result = Polyline(target, points, count); FinishPrimitive(dc, target, saved); return result; }
        static bool CaptureLine(IntPtr dc, int x, int y) { IntPtr target; int saved = PreparePrimitive(dc, out target); bool result = LineTo(target, x, y); FinishPrimitive(dc, target, saved); if (target != dc) MoveToEx(target, x, y, IntPtr.Zero); return result; }
        static bool CaptureMove(IntPtr dc, int x, int y, IntPtr old) { return MoveToEx(Target(dc), x, y, old); }

        public static void Present()
        {
            if (presenting || viewWindow == IntPtr.Zero || uiDc == IntPtr.Zero || mapDc == IntPtr.Zero) return;
            presenting = true;
            IntPtr dc = GetDC(viewWindow);
            try
            {
                if (dc == IntPtr.Zero) return;
                int saved = SaveDC(dc);
                try
                {
                    SelectClipRgn(dc, IntPtr.Zero);
                    if (Active)
                    {
                        // All transfers are 1:1. Only the native map backing
                        // contributes pixels to the enlarged viewport.
                        BitBlt(dc, 10, 58, ClientWidth - 224, ClientHeight - 88, mapDc, 10, 58, Srccopy);
                        BitBlt(dc, 0, 0, 586, 58, uiDc, 0, 0, Srccopy);
                        BitBlt(dc, ClientWidth - 214, 0, 214, 58, uiDc, 586, 0, Srccopy);
                        if (ClientWidth > 800) PatBlt(dc, 586, 0, ClientWidth - 800, 58, 0x42);
                        BitBlt(dc, ClientWidth - 214, 58, 214, 512, uiDc, 586, 58, Srccopy);
                        if (ClientHeight > 600) PatBlt(dc, ClientWidth - 214, 570, 214, ClientHeight - 600, 0x42);
                        BitBlt(dc, 0, ClientHeight - 30, 586, 30, uiDc, 0, 570, Srccopy);
                        BitBlt(dc, ClientWidth - 214, ClientHeight - 30, 214, 30, uiDc, 586, 570, Srccopy);
                        if (ClientWidth > 800) PatBlt(dc, 586, ClientHeight - 30, ClientWidth - 800, 30, 0x42);
                        for (int y = 58; y < ClientHeight - 30; y += 512)
                            BitBlt(dc, 0, y, 10, Math.Min(512, ClientHeight - 30 - y), uiDc, 0, 58, Srccopy);
                    }
                    else
                    {
                        PatBlt(dc, 0, 0, ClientWidth, ClientHeight, 0x42);
                        BitBlt(dc, (ClientWidth - 800) / 2, (ClientHeight - 600) / 2, 800, 600, uiDc, 0, 0, Srccopy);
                    }
                }
                finally { RestoreDC(dc, saved); }
            }
            finally { if (dc != IntPtr.Zero) ReleaseDC(viewWindow, dc); presenting = false; }
        }
        public static Point PhysicalToLogical(Point p, bool map)
        {
            if (!map) { p.X -= (ClientWidth - 800) / 2; p.Y -= (ClientHeight - 600) / 2; return p; }
            if (p.X >= ClientWidth - 214) p.X -= ClientWidth - 800;
            if (p.Y >= ClientHeight - 30) p.Y -= ClientHeight - 600;
            return p;
        }
        public static Point LogicalToPhysical(Point p, bool map)
        {
            if (!map) { p.X += (ClientWidth - 800) / 2; p.Y += (ClientHeight - 600) / 2; return p; }
            if (p.X >= 586) p.X += ClientWidth - 800;
            if (p.Y >= 570) p.Y += ClientHeight - 600;
            return p;
        }
        static bool LogicalClientRect(IntPtr window, out Rect rect)
        {
            bool result = GetClientRect(window, out rect);
            if (window == viewWindow) rect = new Rect { Right = 800, Bottom = 600 };
            return result;
        }
        static bool ToLogicalClient(IntPtr window, ref Point point)
        {
            bool result = ScreenToClient(window, ref point);
            if (result && window == viewWindow)
            {
                Point? proxy = MapCursorToProxy != null ? MapCursorToProxy(point.X, point.Y) : null;
                point = proxy.HasValue ? proxy.Value : PhysicalToLogical(point, Active);
            }
            return result;
        }
        public static Point GetPhysicalCursor()
        {
            Point point;
            if (!GetCursorPos(out point)) return new Point();
            if (viewWindow != IntPtr.Zero) ScreenToClient(viewWindow, ref point);
            return point;
        }
        static bool FromLogicalClient(IntPtr window, ref Point point)
        {
            if (window == viewWindow) point = LogicalToPhysical(point, Active);
            return ClientToScreen(window, ref point);
        }
        static IntPtr ViewProcedure(IntPtr window, uint message, IntPtr wp, IntPtr lp)
        {
            if (message >= 0x200 && message <= 0x209)
            {
                Point p = new Point { X = unchecked((short)lp.ToInt32()), Y = unchecked((short)(lp.ToInt32() >> 16)) };
                bool map = Active;
                // Let the native-input owner finish captures even when the
                // pointer is dragged over UI or outside the map rectangle.
                if (map && MapMouseMessage != null && MapMouseMessage(message, wp, p.X, p.Y)) return IntPtr.Zero;
                p = PhysicalToLogical(p, map);
                lp = new IntPtr((p.Y << 16) | (p.X & 0xffff));
            }
            if (message == 0x14) { Present(); return new IntPtr(1); } // WM_ERASEBKGND
            IntPtr result = CallWindowProcA(viewProcedure, window, message, wp, lp);
            if (message == 0xf)
            {
                // Native BeginPaint has validated the update region. Complete
                // any geometry transition on the same game thread afterward.
                if (mapNeedsRedraw && Active && RenderMap != null) { mapNeedsRedraw = false; RenderMap(); }
                Present();
            }
            return result;
        }
        public static IntPtr DispatchOriginalMouse(uint message, IntPtr wp, int x, int y)
        {
            if (viewProcedure == IntPtr.Zero || viewWindow == IntPtr.Zero) return IntPtr.Zero;
            return CallWindowProcA(viewProcedure, viewWindow, message, wp, new IntPtr((y << 16) | (x & 0xffff)));
        }
        static IntPtr GameProcedure(IntPtr window, uint message, IntPtr wp, IntPtr lp)
        {
            WindowPosition incoming = new WindowPosition();
            bool preservePosition = message == 0x46 && lp != IntPtr.Zero;
            if (preservePosition) incoming = (WindowPosition)Marshal.PtrToStructure(lp, typeof(WindowPosition));
            if (message == 0x24 && lp != IntPtr.Zero)
            {
                MinMaxInfo limits = (MinMaxInfo)Marshal.PtrToStructure(lp, typeof(MinMaxInfo));
                Rect min = new Rect { Right = 800, Bottom = 600 }, max = new Rect { Right = 3840, Bottom = 2160 };
                AdjustWindowRectEx(ref min, WindowStyle, false, 0); AdjustWindowRectEx(ref max, WindowStyle, false, 0);
                limits.MinimumTrack = new Point { X = min.Right - min.Left, Y = min.Bottom - min.Top };
                limits.MaximumTrack = new Point { X = max.Right - max.Left, Y = max.Bottom - max.Top };
                Marshal.StructureToPtr(limits, lp, false);
                return IntPtr.Zero;
            }
            if (message == 5 && wp.ToInt32() != 1) ResizeFromWindow();
            if (message == 0x14) return new IntPtr(1);
            IntPtr result = CallWindowProcA(gameProcedure, window, message, wp, lp);
            // Native fullscreen handlers rewrite WINDOWPOS during user resize.
            // Retain the incoming OS/user request after the original handler.
            if (preservePosition) Marshal.StructureToPtr(incoming, lp, false);
            if (message == 0x82)
            {
                ReleaseBacking(ref mapDc, ref mapBitmap, ref mapOldBitmap);
                ReleaseBacking(ref uiDc, ref uiBitmap, ref uiOldBitmap);
                viewWindow = gameWindow = IntPtr.Zero;
                viewProcedure = gameProcedure = IntPtr.Zero;
            }
            return result;
        }
    }
}
