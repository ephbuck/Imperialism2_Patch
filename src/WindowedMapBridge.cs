using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Globalization;
using Imp2Net.Graphics;

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
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor,Work; public uint Flags; }

        const uint Srccopy = 0x00CC0020, WindowStyle = 0x00CF0000, Visible = 0x10000000;
        const int GwlStyle = -16, GwlWndProc = -4;
        const uint NoSize = 1, NoMove = 2, NoZOrder = 4, FrameChanged = 0x20;
        static IntPtr gameWindow, viewWindow, gameProcedure, viewProcedure;
        static IntPtr uiDc, uiBitmap, uiOldBitmap, mapDc, mapBitmap, mapOldBitmap;
        static IntPtr skinDc,skinBitmap,skinOldBitmap;
        static IntPtr frameDc,frameBitmap,frameOldBitmap;
        static bool initialized, resizing, presenting, mapNeedsRedraw, presentQueued;
        const uint PresentMessage=0x8033;
        [ThreadStatic] static int mapDepth;
        static int requestedWidth = 1280, requestedHeight = 900;
        static double requestedUiScale = 1, sidebarFraction = .20, dpiScale = 1;
        static bool borderless;
        static uint GameStyle { get { return borderless ? 0x80000000 : WindowStyle; } }
        static long geometryGeneration;
        static long mapReadyGeneration=-1;
        static readonly Queue<double> presentTimes=new Queue<double>();
        public static HdLayout Layout { get; private set; }
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
        static WindowedMapBridge() { ClientWidth = 800; ClientHeight = 600; Layout = new HdLayout(800,600,1,1,.2,0); }

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
        [DllImport("user32.dll")] static extern bool PostMessageA(IntPtr window,uint message,IntPtr wp,IntPtr lp);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window, ref Point point);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point,uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfoA(IntPtr monitor,ref MonitorInfo info);
        [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, IntPtr rect, IntPtr brush);
        [DllImport("user32.dll")] static extern int FrameRect(IntPtr dc, IntPtr rect, IntPtr brush);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
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
            // Called before the first game window; dimensions below are client
            // pixels. Failure can mean a manifest/compatibility layer set it.
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
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
                string[] pair = line.Split(new[] { '=' }, 2); double value;
                if (pair.Length != 2 || !Double.TryParse(pair[1].Split(';')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                    || Double.IsNaN(value) || Double.IsInfinity(value)) continue;
                if (pair[0].Trim().Equals("WindowWidth", StringComparison.OrdinalIgnoreCase)) requestedWidth = (int)Math.Max(800, Math.Min(3840, value));
                if (pair[0].Trim().Equals("WindowHeight", StringComparison.OrdinalIgnoreCase)) requestedHeight = (int)Math.Max(600, Math.Min(2160, value));
                if (pair[0].Trim().Equals("UiScale", StringComparison.OrdinalIgnoreCase)) requestedUiScale = Math.Max(.5, Math.Min(4,value));
                if (pair[0].Trim().Equals("SidebarFraction", StringComparison.OrdinalIgnoreCase)) sidebarFraction = Math.Max(.12, Math.Min(.4,value));
                if (pair[0].Trim().Equals("EdgeScrollMargin", StringComparison.OrdinalIgnoreCase)) NativeMapInput.SetEdgeScrollMargin((int)Math.Max(8,Math.Min(128,value)));
                if (pair[0].Trim().Equals("Borderless", StringComparison.OrdinalIgnoreCase)) borderless = value!=0;
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
            if(!VirtualProtect(address, (UIntPtr)4, protection, out ignored)) throw new System.ComponentModel.Win32Exception();
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
            MonitorInfo monitor=new MonitorInfo { Size=Marshal.SizeOf(typeof(MonitorInfo)) };
            IntPtr monitorHandle=gameWindow==IntPtr.Zero ? MonitorFromPoint(new Point(),2) : MonitorFromWindow(gameWindow,2);
            if(GetMonitorInfoA(monitorHandle,ref monitor)) workArea=borderless ? monitor.Monitor : monitor.Work;
            else if (!SystemParametersInfoA(48, 0, out workArea, 0) || workArea.Right <= workArea.Left || workArea.Bottom <= workArea.Top)
                workArea = new Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            if(borderless) { x=workArea.Left;y=workArea.Top;outerW=Math.Min(3840,workArea.Right-x);outerH=Math.Min(2160,workArea.Bottom-y);return; }
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
                style = GameStyle | Visible | 0x02000000;
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
            SetWindowLongA(gameWindow, GwlStyle, new IntPtr(unchecked((int)(GameStyle | Visible | 0x02000000))));
            gameProcedure = SetWindowLongA(gameWindow, GwlWndProc, Marshal.GetFunctionPointerForDelegate(gameHook));
            int x, y, outerW, outerH;
            ComputeCenteredFrame(GameStyle, false, 0, out x, out y, out outerW, out outerH);
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
                value = new IntPtr(unchecked((int)(GameStyle | Visible | 0x02000000)));
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
        static void ResizeFromWindow(bool force=false)
        {
            if (resizing || gameWindow == IntPtr.Zero) return;
            Rect client;
            if (!GetClientRect(gameWindow, out client) || client.Right < 800 || client.Bottom < 600) return;
            int width = Math.Min(3840, client.Right), height = Math.Min(2160, client.Bottom);
            double nextDpi = 1;
            try { uint dpi = GetDpiForWindow(gameWindow); if (dpi != 0) nextDpi = Math.Max(.5,Math.Min(4,dpi / 96.0)); } catch (EntryPointNotFoundException) { }
            if (!force && width == ClientWidth && height == ClientHeight && nextDpi == dpiScale && mapDc != IntPtr.Zero) return;
            resizing = true;
            try
            {
                ReplaceMapBacking(width, height);
                ClientWidth = width; ClientHeight = height;
                dpiScale = nextDpi;
                Layout = new HdLayout(width,height,requestedUiScale,dpiScale,sidebarFraction,++geometryGeneration);
                NativeMapRenderer.Log("Layout " + Layout);
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
                dc=bitmap=IntPtr.Zero;
                throw new InvalidOperationException("Cannot allocate window presentation backing.");
            }
            previous = SelectObject(dc, bitmap);
            if(previous==IntPtr.Zero || previous==new IntPtr(-1) || !PatBlt(dc, 0, 0, width, height, 0x42))
            {
                if(previous!=IntPtr.Zero && previous!=new IntPtr(-1)) SelectObject(dc,previous);
                DeleteObject(bitmap);DeleteDC(dc);dc=bitmap=previous=IntPtr.Zero;
                throw new InvalidOperationException("Cannot initialize window presentation backing.");
            }
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
                IntPtr nextFrameDc=IntPtr.Zero,nextFrameBitmap=IntPtr.Zero,nextFrameOld=IntPtr.Zero;
                MakeBacking(reference, width, height, out dc, out bitmap, out previous);
                try
                {
                    MakeBacking(reference,width,height,out nextFrameDc,out nextFrameBitmap,out nextFrameOld);
                    if (uiDc == IntPtr.Zero) MakeBacking(reference, 800, 600, out uiDc, out uiBitmap, out uiOldBitmap);
                }
                catch {
                    ReleaseBacking(ref dc,ref bitmap,ref previous);
                    ReleaseBacking(ref nextFrameDc,ref nextFrameBitmap,ref nextFrameOld);
                    throw;
                }
                ReleaseBacking(ref mapDc, ref mapBitmap, ref mapOldBitmap);
                ReleaseBacking(ref frameDc,ref frameBitmap,ref frameOldBitmap);
                mapDc = dc; mapBitmap = bitmap; mapOldBitmap = previous;
                frameDc=nextFrameDc;frameBitmap=nextFrameBitmap;frameOldBitmap=nextFrameOld;
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
        public static void MapFrameReady() { mapReadyGeneration=Layout.Generation;mapNeedsRedraw=false; }
        static void EnsureMapFrame()
        {
            if(!resizing && Active && RenderMap!=null && (mapNeedsRedraw || mapReadyGeneration!=Layout.Generation))
            { mapNeedsRedraw=false;RenderMap(); }
        }
        public static void EndMapDraw() { if (mapDepth > 0) --mapDepth; if (mapDepth == 0) RequestPresent(); }
        static void RequestPresent()
        {
            if(viewWindow==IntPtr.Zero || presentQueued) return;
            presentQueued=true;
            if(!PostMessageA(viewWindow,PresentMessage,IntPtr.Zero,IntPtr.Zero)) presentQueued=false;
        }
        public static void PresentNativeMap(IntPtr sourceDc, int sx, int sy, int width, int height, int x, int y)
        {
            EnsureBacking();
            if (mapDc != IntPtr.Zero) BitBlt(mapDc, x, y, width, height, sourceDc, sx, sy, Srccopy);
            if (mapDepth == 0) RequestPresent();
        }
        public static void PresentNativeMapDib(IntPtr bits, IntPtr bitmapInfo, int sx, int sy, int width, int height, int x, int y)
        {
            EnsureBacking();
            int bitmapHeight = Marshal.ReadInt32(bitmapInfo, 8);
            int sourceY = bitmapHeight > 0 ? bitmapHeight - sy - height : sy;
            if (mapDc != IntPtr.Zero) StretchDIBits(mapDc, x, y, width, height, sx, sourceY, width, height, bits, bitmapInfo, 0, Srccopy);
            if (mapDepth == 0) RequestPresent();
        }
        static void Pane(IntPtr dc, PixelRect dest, int sx, int sy, int sw, int sh)
        { StretchBlt(dc,dest.Left,dest.Top,dest.Width,dest.Height,uiDc,sx,sy,sw,sh,Srccopy); }
        // Repeat only a narrow, control-free strip of native background. Draw
        // the complete control group over it afterward; never stretch a group
        // to fill an unrelated aspect ratio or repeat interactive controls.
        static void HorizontalBackground(IntPtr dc, int left, int right, int top, int height, int sourceY, int sourceHeight)
        {
            int step = Math.Max(1,Layout.Edge(8));
            for (int x=left;x<right;x+=step)
                StretchBlt(dc,x,top,Math.Min(step,right-x),height,uiDc,32,sourceY,8,sourceHeight,Srccopy);
        }
        static bool CaptureBitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint rop)
        {
            bool screen = IsScreen(dest);
            bool result = BitBlt(Target(dest), x, y, w, h, Target(source), sx, sy, rop);
            if (screen && mapDepth == 0) RequestPresent();
            return result;
        }
        static int CaptureDib(IntPtr dest, int x, int y, int w, int h, int sx, int sy, int sw, int sh, IntPtr bits, IntPtr info, uint usage, uint rop)
        {
            bool screen = IsScreen(dest);
            int result = StretchDIBits(Target(dest), x, y, w, h, sx, sy, sw, sh, bits, info, usage, rop);
            if (screen && mapDepth == 0) RequestPresent();
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
            if (target != original && mapDepth == 0) RequestPresent();
        }
        static int CaptureFill(IntPtr dc, IntPtr rect, IntPtr brush) { int r = FillRect(Target(dc), rect, brush); if (IsScreen(dc) && mapDepth == 0) RequestPresent(); return r; }
        static int CaptureFrame(IntPtr dc, IntPtr rect, IntPtr brush) { int r = FrameRect(Target(dc), rect, brush); if (IsScreen(dc) && mapDepth == 0) RequestPresent(); return r; }
        static bool CaptureRectangle(IntPtr dc, int l, int t, int r, int b) { IntPtr target; int saved = PreparePrimitive(dc, out target); bool result = Rectangle(target, l, t, r, b); FinishPrimitive(dc, target, saved); return result; }
        static bool CapturePolyline(IntPtr dc, IntPtr points, int count) { IntPtr target; int saved = PreparePrimitive(dc, out target); bool result = Polyline(target, points, count); FinishPrimitive(dc, target, saved); return result; }
        static bool CaptureLine(IntPtr dc, int x, int y) { IntPtr target; int saved = PreparePrimitive(dc, out target); bool result = LineTo(target, x, y); FinishPrimitive(dc, target, saved); if (target != dc) MoveToEx(target, x, y, IntPtr.Zero); return result; }
        static bool CaptureMove(IntPtr dc, int x, int y, IntPtr old) { return MoveToEx(Target(dc), x, y, old); }

        public static void Present()
        {
            if (presenting || mapDepth>0 || viewWindow == IntPtr.Zero || uiDc == IntPtr.Zero || mapDc == IntPtr.Zero) return;
            presenting = true;
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            IntPtr dc = GetDC(viewWindow);
            try
            {
                if (dc == IntPtr.Zero) return;
                EnsureMapFrame();
                if(Active && mapReadyGeneration!=Layout.Generation) return;
                if(!Active) ReleaseBacking(ref skinDc,ref skinBitmap,ref skinOldBitmap);
                PresentFrame(dc,Active);
            }
            finally { if (dc != IntPtr.Zero) ReleaseDC(viewWindow, dc); presenting = false;
                presentTimes.Enqueue((System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency);
                if(presentTimes.Count>600)presentTimes.Dequeue(); }
        }
        // All background and control draws stay in memory. Publishing exactly
        // one completed image prevents visible erase/repaint phases, including
        // the many texture tiles used to extend the sidebar and menu margins.
        internal static void PresentFrame(IntPtr destination,bool active)
        {
            if(frameDc==IntPtr.Zero) return;
            ComposeFrame(frameDc,active);
            if(!BitBlt(destination,0,0,ClientWidth,ClientHeight,frameDc,0,0,Srccopy))
                throw new System.ComponentModel.Win32Exception();
        }
        // Kept separate from GetDC/ReleaseDC so the real GDI compositor can be
        // checked against synthetic source pixels without starting the game.
        internal static void ComposeFrame(IntPtr dc,bool active)
        {
                int saved = SaveDC(dc);
                try
                {
                    SelectClipRgn(dc, IntPtr.Zero);
                    if (active)
                    {
                        if(skinDc==IntPtr.Zero) {
                            MakeBacking(dc,800,600,out skinDc,out skinBitmap,out skinOldBitmap);
                            BitBlt(skinDc,0,0,800,600,uiDc,0,0,Srccopy);
                        }
                        HdLayout layout = Layout;
                        PixelRect map = layout.Map;
                        // The map backing retains the native screen origin;
                        // placement translates it, never scales the terrain.
                        BitBlt(dc,map.Left,map.Top,map.Width,map.Height,mapDc,10,58,Srccopy);
                        SetStretchBltMode(dc,3); // COLORONCOLOR: deterministic bitmap UI scaling
                        HorizontalBackground(dc,0,map.Right,0,map.Top,0,58);
                        HorizontalBackground(dc,0,map.Right,map.Bottom,ClientHeight-map.Bottom,570,30);
                        Pane(dc,layout.Top,0,0,586,58);
                        Pane(dc,layout.Bottom,0,570,586,30);
                        for (int y=map.Top;y<map.Bottom;y+=layout.Edge(512))
                            StretchBlt(dc,0,y,map.Left,Math.Min(layout.Edge(512),map.Bottom-y),uiDc,0,58,10,
                                Math.Min(512,(int)Math.Ceiling((map.Bottom-y)/layout.Scale)),Srccopy);
                        // The first map may already have a selected unit. Its
                        // y=400 strip contains labels/icons, so it cannot serve
                        // as a background. Repeat only the small empty texture
                        // left of the portrait, with separate stone side strips.
                        int textureStep=Math.Max(1,layout.Edge(16));
                        for(int y=0;y<ClientHeight;y+=textureStep) {
                            int stripHeight=Math.Min(textureStep,ClientHeight-y);
                            for(int x=map.Right;x<ClientWidth;x+=textureStep)
                                StretchBlt(dc,x,y,Math.Min(textureStep,ClientWidth-x),stripHeight,skinDc,612,334,16,16,Srccopy);
                            StretchBlt(dc,map.Right,y,layout.Edge(20),stripHeight,skinDc,586,350,20,16,Srccopy);
                            StretchBlt(dc,ClientWidth-layout.Edge(32),y,layout.Edge(32),stripHeight,skinDc,768,350,32,16,Srccopy);
                        }
                        // The complete column includes unit information below
                        // y=540. Preserve that center content at the same scale;
                        // only the help/end-turn feet belong at the bottom.
                        Pane(dc,layout.SidebarMain,586,0,214,540);
                        Pane(dc,layout.SidebarDataFoot,620,540,144,52);
                        Pane(dc,layout.SidebarLeftFoot,586,540,34,60);
                        Pane(dc,layout.SidebarRightFoot,764,540,36,60);
                        Pane(dc,layout.SidebarBottomBorder,620,592,144,8);
                    }
                    else
                    {
                        PatBlt(dc, 0, 0, ClientWidth, ClientHeight, 0x42);
                        SetStretchBltMode(dc,3);
                        Pane(dc,Layout.Menu,0,0,800,600);
                    }
                }
                finally { RestoreDC(dc, saved); }
        }
        public static Point PhysicalToLogical(Point p, bool map)
        {
            GeometryPoint result;
            Layout.TryUiToLogical(p.X,p.Y,map,out result);
            return new Point { X=(int)Math.Floor(result.X), Y=(int)Math.Floor(result.Y) };
        }
        public static Point LogicalToPhysical(Point p, bool map)
        {
            GeometryPoint result=Layout.UiToPhysical(p.X,p.Y,map);
            return new Point { X=(int)Math.Round(result.X), Y=(int)Math.Round(result.Y) };
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
            if(message==PresentMessage) { presentQueued=false; Present(); return IntPtr.Zero; }
            if(HandleHotkey(message,wp,lp)) return IntPtr.Zero;
            if (message >= 0x200 && message <= 0x209)
            {
                EnsureMapFrame();
                Point p = new Point { X = unchecked((short)lp.ToInt32()), Y = unchecked((short)(lp.ToInt32() >> 16)) };
                bool map = Active;
                // Let the native-input owner finish captures even when the
                // pointer is dragged over UI or outside the map rectangle.
                if (map && MapMouseMessage != null && MapMouseMessage(message, wp, p.X, p.Y)) return IntPtr.Zero;
                p = PhysicalToLogical(p, map);
                lp = new IntPtr((p.Y << 16) | (p.X & 0xffff));
            }
            // Erase can arrive inside native BeginPaint, before its UI update
            // has completed. The completed WM_PAINT frame covers the client.
            if (message == 0x14) return new IntPtr(1); // WM_ERASEBKGND
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
        static void CaptureDiagnostics()
        {
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"hd-captures");
            Directory.CreateDirectory(folder);
            string prefix=Path.Combine(folder,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            if(uiBitmap!=IntPtr.Zero) using(var image=System.Drawing.Image.FromHbitmap(uiBitmap)) image.Save(prefix+"-ui.png");
            if(mapBitmap!=IntPtr.Zero) using(var image=System.Drawing.Image.FromHbitmap(mapBitmap)) image.Save(prefix+"-map.png");
            using(var frame=new System.Drawing.Bitmap(ClientWidth,ClientHeight,System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using(var graphics=System.Drawing.Graphics.FromImage(frame)) {
                IntPtr dc=graphics.GetHdc();try { ComposeFrame(dc,Active); } finally { graphics.ReleaseHdc(dc); }
                frame.Save(prefix+"-frame.png",System.Drawing.Imaging.ImageFormat.Png);
            }
            double[] times=presentTimes.ToArray();Array.Sort(times);
            using(var process=System.Diagnostics.Process.GetCurrentProcess())
                File.WriteAllText(prefix+"-geometry.txt",Layout+Environment.NewLine+NativeMapRenderer.Diagnostics()+Environment.NewLine+
                    String.Format(CultureInfo.InvariantCulture,"generation={0}, dpiScale={1:F3}, presentSamples={2}, presentP50Ms={3:F3}, presentP95Ms={4:F3}, privateBytes={5}, workingSetBytes={6}, gdiHandles={7}",
                        Layout.Generation,dpiScale,times.Length,times.Length==0?0:times[(times.Length-1)/2],times.Length==0?0:times[(int)((times.Length-1)*.95)],
                        process.PrivateMemorySize64,process.WorkingSet64,GetGuiResources(process.Handle,0)));
            NativeMapRenderer.Log("Captured diagnostics: "+prefix);
        }
        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process,uint flags);
        public static IntPtr DispatchOriginalMouse(uint message, IntPtr wp, int x, int y)
        {
            if (viewProcedure == IntPtr.Zero || viewWindow == IntPtr.Zero) return IntPtr.Zero;
            return CallWindowProcA(viewProcedure, viewWindow, message, wp, new IntPtr((y << 16) | (x & 0xffff)));
        }
        static IntPtr GameProcedure(IntPtr window, uint message, IntPtr wp, IntPtr lp)
        {
            if(HandleHotkey(message,wp,lp)) return IntPtr.Zero;
            WindowPosition incoming = new WindowPosition();
            bool preservePosition = message == 0x46 && lp != IntPtr.Zero;
            if (preservePosition) incoming = (WindowPosition)Marshal.PtrToStructure(lp, typeof(WindowPosition));
            if (message == 0x24 && lp != IntPtr.Zero)
            {
                MinMaxInfo limits = (MinMaxInfo)Marshal.PtrToStructure(lp, typeof(MinMaxInfo));
                Rect min = new Rect { Right = 800, Bottom = 600 }, max = new Rect { Right = 3840, Bottom = 2160 };
                AdjustWindowRectEx(ref min, GameStyle, false, 0); AdjustWindowRectEx(ref max, GameStyle, false, 0);
                limits.MinimumTrack = new Point { X = min.Right - min.Left, Y = min.Bottom - min.Top };
                limits.MaximumTrack = new Point { X = max.Right - max.Left, Y = max.Bottom - max.Top };
                Marshal.StructureToPtr(limits, lp, false);
                return IntPtr.Zero;
            }
            if (message == 5 && wp.ToInt32() != 1) ResizeFromWindow();
            if (message == 0x2e0) ResizeFromWindow(); // WM_DPICHANGED, re-read actual client pixels
            if (message == 0x14) return new IntPtr(1);
            IntPtr result = CallWindowProcA(gameProcedure, window, message, wp, lp);
            // Native fullscreen handlers rewrite WINDOWPOS during user resize.
            // Retain the incoming OS/user request after the original handler.
            if (preservePosition) Marshal.StructureToPtr(incoming, lp, false);
            if (message == 0x82)
            {
                ReleaseBacking(ref mapDc, ref mapBitmap, ref mapOldBitmap);
                ReleaseBacking(ref uiDc, ref uiBitmap, ref uiOldBitmap);
                ReleaseBacking(ref skinDc,ref skinBitmap,ref skinOldBitmap);
                ReleaseBacking(ref frameDc,ref frameBitmap,ref frameOldBitmap);
                viewWindow = gameWindow = IntPtr.Zero;
                viewProcedure = gameProcedure = IntPtr.Zero;
            }
            return result;
        }
        static bool HandleHotkey(uint message,IntPtr wp,IntPtr lp)
        {
            if((message!=0x100 && message!=0x104) || (lp.ToInt32()&0x40000000)!=0) return false;
            int key=wp.ToInt32();
            if(key==0x78) { CaptureDiagnostics();return true; } // F9
            if(key!=0x77 && key!=0x7a) return false;
            if(key==0x77) ReadConfiguration(); else borderless=!borderless; // F8 / F11
            SetWindowLongA(gameWindow,GwlStyle,new IntPtr(unchecked((int)(GameStyle|Visible|0x02000000))));
            int x,y,w,h;ComputeCenteredFrame(GameStyle,false,0,out x,out y,out w,out h);
            SetWindowPos(gameWindow,IntPtr.Zero,x,y,w,h,NoZOrder|FrameChanged|0x40);
            ResizeFromWindow(true);
            return true;
        }
    }
}
