using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Imp2Net;

// Headless x86 regression tests: exercise the real hooks and pixel sampler.
internal static class TestMapAnimation
{
    const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    static int passed;
    static Type Bridge { get { return typeof(GraphicsBridge); } }
    static void Set(string name, object value) { Bridge.GetField(name, PrivateStatic).SetValue(null, value); }
    static object Call(string name, params object[] args) { return Bridge.GetMethod(name, PrivateStatic).Invoke(null, args); }
    static void Geometry(string name, int value) { Bridge.GetProperty(name).GetSetMethod(true).Invoke(null, new object[] { value }); }
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    static RECT R(int l, int t, int r, int b) { return new RECT { Left = l, Top = t, Right = r, Bottom = b }; }
    static bool Same(RECT a, RECT b) { return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom; }

    static RECT captured;
    static IntPtr nativeRect;
    static bool throwFromTick;
    static bool CaptureInvalidation(IntPtr hwnd, IntPtr rect, bool erase)
    {
        captured = (RECT)Marshal.PtrToStructure(rect, typeof(RECT));
        return true;
    }
    static void NativeTick(IntPtr animation)
    {
        // Controlled stand-in for the engine callback; these tests do not run the game scheduler.
        Marshal.WriteInt16(animation, 8, (short)(Marshal.ReadInt16(animation, 8) + 1));
        Call("MyInvalidateRectHook", (IntPtr)123, nativeRect, false);
        if (throwFromTick) throw new Exception("native test failure");
    }

    static void TestTickInvalidation()
    {
        Geometry("TargetW", 2880); Geometry("TargetH", 2160); Geometry("DstX", 480); Geometry("DstY", 0);
        Set("s_viewHwnd", (IntPtr)123); Set("s_pActiveMapDialog", (IntPtr)456);
        Set("s_origInvalidateRect", new GraphicsBridge.InvalidateRectDelegate(CaptureInvalidation));
        Type tickType = Bridge.GetNestedType("AnimationTickDelegate", BindingFlags.NonPublic);
        Delegate tick = Delegate.CreateDelegate(tickType, typeof(TestMapAnimation).GetMethod("NativeTick", PrivateStatic));
        IDictionary originals = (IDictionary)Bridge.GetField("s_originalAnimationTicks", PrivateStatic).GetValue(null);
        originals.Add((IntPtr)0x6F6DE8, tick);
        IntPtr animation = Marshal.AllocHGlobal(64);
        nativeRect = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(new byte[64], 0, animation, 64);
            Marshal.WriteIntPtr(animation, (IntPtr)0x6F6DE8);
            Marshal.WriteIntPtr(animation, 4, (IntPtr)456);
            Marshal.StructureToPtr(R(1002, 314, 1066, 378), nativeRect, false);
            GraphicsBridge.SetZoom(0.5f);
            Call("MyAnimationTickHook", animation);
            // Original X=1002 is outside 800x600 but maps to visible X=506.
            Check(Same(captured, R(2301, 669, 2417, 785)), "native tick invalidates the visible zoomed tile beyond X=800");
            Check(Marshal.ReadInt16(animation, 8) == 1, "original tick callback invoked without mouse messages");
            Check(!(bool)Bridge.GetField("s_insideMapAnimation", PrivateStatic).GetValue(null), "animation scope restored after tick");
            Check(GraphicsBridge.GetAnimationDiagnostics().Contains("frameChanges=1"), "diagnostics observe the native frame change");

            GraphicsBridge.SetZoom(1.0f);
            Call("MyAnimationTickHook", animation);
            Check(captured.Left == 3360 && captured.Right == 3360, "1x invalidation retains existing canvas clipping");
            GraphicsBridge.SetZoom(0.5f);
            Marshal.WriteIntPtr(animation, 4, (IntPtr)999);
            Call("MyAnimationTickHook", animation);
            Check(captured.Left == 3360, "non-map animations keep their own geometry");
            Marshal.WriteIntPtr(animation, 4, (IntPtr)456);
            throwFromTick = true;
            try { Call("MyAnimationTickHook", animation); } catch (TargetInvocationException) { }
            Check(!(bool)Bridge.GetField("s_insideMapAnimation", PrivateStatic).GetValue(null), "animation scope restored after exception");
        }
        finally
        {
            throwFromTick = false; originals.Clear();
            Marshal.FreeHGlobal(animation); Marshal.FreeHGlobal(nativeRect);
            Set("s_viewHwnd", IntPtr.Zero); Set("s_pActiveMapDialog", IntPtr.Zero);
        }
    }

    static POINT tilePoint;
    static POINT routedPoint;
    static readonly GraphicsBridge.WndProcDelegate captureWndProc = CaptureWndProc;
    static IntPtr CaptureWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        routedPoint = new POINT { X = (short)(lParam.ToInt32() & 0xFFFF), Y = (short)(lParam.ToInt32() >> 16) };
        return IntPtr.Zero;
    }
    static bool NativeScreenToClient(IntPtr hwnd, ref POINT pt) { return true; }
    static bool selectionScoped;
    static RECT retainedBounds;
    static int retainedDeltaX, retainedDeltaY;
    static void NativeLocations(IntPtr animator, int deltaX, int deltaY, RECT bounds)
    {
        retainedBounds = bounds; retainedDeltaX = deltaX; retainedDeltaY = deltaY;
    }
    static void TestAnimationRetention()
    {
        Type locationType = Bridge.GetNestedType("AnimationLocationsDelegate", BindingFlags.NonPublic);
        Set("s_originalAnimationLocations", Delegate.CreateDelegate(locationType, typeof(TestMapAnimation).GetMethod("NativeLocations", PrivateStatic)));
        RECT original = R(-64, -64, 640, 576);
        foreach (float zoom in new float[] { 1.0f, 0.75f, 0.5f, 0.25f })
        {
            GraphicsBridge.SetZoom(zoom);
            Call("MyAnimationLocationsHook", (IntPtr)789, -32, 64, original);
            int right = (int)Math.Ceiling(576.0 / zoom) + 64;
            int bottom = (int)Math.Ceiling(512.0 / zoom) + 64;
            Check(Same(retainedBounds, R(-64, -64, right, bottom)), "camera panning retains animations across the visible map at zoom=" + zoom);
        }
        Check(retainedDeltaX == -32 && retainedDeltaY == 64, "animation retention preserves native camera deltas");
    }
    static void NativeTileLookup(IntPtr view, IntPtr point, IntPtr col, IntPtr row, IntPtr valid)
    {
        tilePoint = (POINT)Marshal.PtrToStructure(point, typeof(POINT));
    }
    static void NativeSelectionDraw(IntPtr view)
    {
        selectionScoped = (bool)Bridge.GetProperty("IsZoomedMapAnimation", PrivateStatic).GetValue(null, null);
        if (throwFromTick) throw new Exception("selection test failure");
    }
    static void TestMapInteractions()
    {
        Set("s_origMapScreenToTile", new GraphicsBridge.MapScreenToTileDelegate(NativeTileLookup));
        Set("s_origMapDrawSelection", new GraphicsBridge.MapDrawSelectionDelegate(NativeSelectionDraw));
        IntPtr point = Marshal.AllocHGlobal(8);
        try
        {
            foreach (float zoom in new float[] { 0.5f, 0.25f })
            {
                GraphicsBridge.SetZoom(zoom);
                GraphicsBridge.LastSx0 = 64; GraphicsBridge.LastSy0 = 64;
                GraphicsBridge.LastScaleX = 1.0 / zoom; GraphicsBridge.LastScaleY = 1.0 / zoom;
                // Original global anchor (362,314) is map-local (352,256).
                // Native pane routing removes (10,58) before ScreenToTile.
                POINT display = new POINT { X = 10 + (int)(352 * zoom), Y = 58 + (int)(256 * zoom) };
                POINT local = new POINT { X = display.X - 10, Y = display.Y - 58 };
                Marshal.StructureToPtr(local, point, false);
                Call("MyMapScreenToTileHook", (IntPtr)456, point, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Check(tilePoint.X == 352 && tilePoint.Y == 256, "rendered explorer maps back to the same native terrain point at zoom=" + zoom);
                POINT unchanged = (POINT)Marshal.PtrToStructure(point, typeof(POINT));
                Check(unchanged.X == local.X && unchanged.Y == local.Y, "map lookup leaves the caller's local point unchanged at zoom=" + zoom);
            }
            GraphicsBridge.SetZoom(0.5f);
            GraphicsBridge.LastScaleX = 2; GraphicsBridge.LastScaleY = 2;
            Marshal.StructureToPtr(new POINT { X = 512, Y = 384 }, point, false);
            Call("MyMapScreenToTileHook", (IntPtr)456, point, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Check(tilePoint.X == 1024 && tilePoint.Y == 768, "visible map click reaches terrain beyond original 800x600 canvas");
            Geometry("TargetW", 2880); Geometry("TargetH", 2160); Geometry("DstX", 480); Geometry("DstY", 0);
            Set("s_viewHwnd", (IntPtr)123);
            Set("s_origViewWndProc", Marshal.GetFunctionPointerForDelegate(captureWndProc));
            Call("ViewWndProc", (IntPtr)123, 0x0201u, IntPtr.Zero, (IntPtr)((1591 << 16) | 2359));
            Check(routedPoint.X == 522 && routedPoint.Y == 442, "4K map mouse message stays inside canvas for native pane routing");
            Set("s_origScreenToClient", new GraphicsBridge.ScreenToClientDelegate(NativeScreenToClient));
            object[] screenArgs = { (IntPtr)123, new POINT { X = 2359, Y = 1591 } };
            Call("MyScreenToClientHook", screenArgs);
            POINT clientPoint = (POINT)screenArgs[1];
            Check(clientPoint.X == 522 && clientPoint.Y == 442, "ScreenToClient polling agrees with routed mouse messages without early map expansion");
            Marshal.StructureToPtr(new POINT { X = routedPoint.X - 10, Y = routedPoint.Y - 58 }, point, false);
            Call("MyMapScreenToTileHook", (IntPtr)456, point, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Check(tilePoint.X == 1024 && tilePoint.Y == 768, "4K input expands only at native tile lookup after native pane localization");
            Marshal.StructureToPtr(new POINT { X = 620, Y = 300 }, point, false);
            Call("MyMapScreenToTileHook", (IntPtr)456, point, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Check(tilePoint.X == 620 && tilePoint.Y == 300, "sidebar coordinates are not expanded as terrain");
            Call("MyMapDrawSelectionHook", (IntPtr)456);
            Check(selectionScoped, "synchronous selection blits use zoomed animation geometry outside Tick");
            Check(!(bool)Bridge.GetField("s_insideMapAnimation", PrivateStatic).GetValue(null), "selection scope restored");
            throwFromTick = true;
            try { Call("MyMapDrawSelectionHook", (IntPtr)456); } catch (TargetInvocationException) { }
            Check(!(bool)Bridge.GetField("s_insideMapAnimation", PrivateStatic).GetValue(null), "selection scope restored after exception");
        }
        finally
        {
            throwFromTick = false; Marshal.FreeHGlobal(point); GraphicsBridge.SetZoom(1.0f);
            Set("s_viewHwnd", IntPtr.Zero); Set("s_origViewWndProc", IntPtr.Zero);
        }
    }

    sealed class PixMap : IDisposable
    {
        public IntPtr Bits, Bmi, Dib, Map;
        public int Width, Height, Stride;
        bool bottomUp;
        public PixMap(int width, int height, bool bottom, bool rgb565)
        {
            Width = width; Height = height; Stride = (width * 2 + 3) & ~3; bottomUp = bottom;
            Bits = Alloc(Stride * height); Bmi = Alloc(52); Dib = Alloc(32); Map = Alloc(36);
            Marshal.WriteInt32(Bmi, 40); Marshal.WriteInt32(Bmi, 4, width); Marshal.WriteInt32(Bmi, 8, bottom ? height : -height);
            Marshal.WriteInt16(Bmi, 12, 1); Marshal.WriteInt16(Bmi, 14, 16); Marshal.WriteInt32(Bmi, 16, rgb565 ? 3 : 0);
            Marshal.WriteInt32(Bmi, 40, rgb565 ? 0xF800 : 0x7C00); Marshal.WriteInt32(Bmi, 44, rgb565 ? 0x7E0 : 0x3E0); Marshal.WriteInt32(Bmi, 48, 0x1F);
            Marshal.WriteIntPtr(Dib, 12, Bits); Marshal.WriteIntPtr(Dib, 16, Bmi); Marshal.WriteIntPtr(Map, 28, Dib);
        }
        static IntPtr Alloc(int size) { IntPtr p = Marshal.AllocHGlobal(size); Marshal.Copy(new byte[size], 0, p, size); return p; }
        public short Pixel(int x, int y) { return Marshal.ReadInt16(Bits, (bottomUp ? Height - 1 - y : y) * Stride + x * 2); }
        public void Pixel(int x, int y, short value) { Marshal.WriteInt16(Bits, (bottomUp ? Height - 1 - y : y) * Stride + x * 2, value); }
        public void Dispose() { Marshal.FreeHGlobal(Bits); Marshal.FreeHGlobal(Bmi); Marshal.FreeHGlobal(Dib); Marshal.FreeHGlobal(Map); }
    }

    static void TestDirtyPixels()
    {
        GraphicsBridge.SetZoom(0.75f);
        Geometry("TargetW", 0); // Headless: suppress presentation while testing the pixel sampler.
        IntPtr srcRect = Marshal.AllocHGlobal(16), dstRect = Marshal.AllocHGlobal(16);
        try
        {
            foreach (bool bottom in new bool[] { false, true })
            foreach (bool rgb565 in new bool[] { false, true })
            using (PixMap src = new PixMap(900, 900, bottom, rgb565))
            using (PixMap dirty = new PixMap(800, 600, !bottom, rgb565))
            using (PixMap full = new PixMap(800, 600, !bottom, rgb565))
            {
                for (int y = 0; y < src.Height; y++) for (int x = 0; x < src.Width; x++)
                    src.Pixel(x, y, (short)((x * 31 + y * 97) & 0x7FFF));
                // Asymmetric origin and fractional scale expose swapped axes and patch-local resampling.
                Marshal.StructureToPtr(R(74, 122, 842, 805), srcRect, false);
                foreach (ZoomFilter filter in new ZoomFilter[] { ZoomFilter.Nearest, ZoomFilter.Smooth, ZoomFilter.Sharp })
                {
                    GraphicsBridge.CurrentZoomFilter = filter;
                    GraphicsBridge.DownsampleMapBuffer(src.Map, dirty.Map, srcRect, dstRect);
                    // Include a partially visible left-edge patch and an asymmetric interior patch.
                    foreach (RECT patch in new RECT[] { R(50, 211, 114, 275), R(500, 340, 564, 404), R(820, 710, 884, 774) })
                    {
                        for (int y = Math.Max(0, patch.Top); y < Math.Min(src.Height, patch.Bottom); y++)
                            for (int x = Math.Max(0, patch.Left); x < Math.Min(src.Width, patch.Right); x++) src.Pixel(x, y, (short)(rgb565 ? 0x07E0 : 0x03E0));
                        Marshal.StructureToPtr(patch, dstRect, false);
                        Call("MyMapSelectionCopyBitsHook", src.Map, dirty.Map, dstRect, IntPtr.Zero, 0, IntPtr.Zero);
                        GraphicsBridge.DownsampleMapBuffer(src.Map, full.Map, srcRect, dstRect);
                        bool equal = true;
                        for (int y = 0; y < 600 && equal; y++) for (int x = 0; x < 800; x++)
                            if (dirty.Pixel(x, y) != full.Pixel(x, y)) { equal = false; break; }
                        Check(equal, "dirty update equals full render: " + filter + " bottomUp=" + bottom + " RGB565=" + rgb565 + " patchX=" + patch.Left);
                    }
                }
            }
            RECT canvas;
            Check(!GraphicsBridge.TryMapBufferPatch(R(9000, 9000, 9064, 9064), out canvas), "offscreen patches rejected");
            Check(!GraphicsBridge.TryMapBufferPatch(R(0, 0, 0, 64), out canvas), "empty source rectangle rejected");
        }
        finally { Marshal.FreeHGlobal(srcRect); Marshal.FreeHGlobal(dstRect); }
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, IntPtr bmi, uint use, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
    static void TestDirectBlit()
    {
        IntPtr bmi = Marshal.AllocHGlobal(40);
        Marshal.Copy(new byte[40], 0, bmi, 40);
        Marshal.WriteInt32(bmi, 40); Marshal.WriteInt32(bmi, 4, 800); Marshal.WriteInt32(bmi, 8, -600);
        Marshal.WriteInt16(bmi, 12, 1); Marshal.WriteInt16(bmi, 14, 32);
        IntPtr src = CreateCompatibleDC(IntPtr.Zero), dst = CreateCompatibleDC(IntPtr.Zero), srcBits, dstBits;
        IntPtr srcBmp = CreateDIBSection(src, bmi, 0, out srcBits, IntPtr.Zero, 0);
        IntPtr dstBmp = CreateDIBSection(dst, bmi, 0, out dstBits, IntPtr.Zero, 0);
        IntPtr oldSrc = SelectObject(src, srcBmp), oldDst = SelectObject(dst, dstBmp);
        try
        {
            Marshal.Copy(new byte[800 * 600 * 4], 0, dstBits, 800 * 600 * 4);
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) Marshal.WriteInt32(srcBits, (y * 800 + x) * 4, 0x00FF0000);
            Geometry("TargetW", 800); Geometry("TargetH", 600); Geometry("DstX", 0); Geometry("DstY", 0);
            GraphicsBridge.SetZoom(0.5f);
            Check((bool)Call("PresentMapAnimation", dst, 1002, 314, 64, 64, src, 0, 0, 0x00CC0020u), "direct animation blit succeeds");
            Check(GetPixel(dst, 506, 186) == 0x000000FF && GetPixel(dst, 537, 217) == 0x000000FF,
                "native opaque corner color preserved in the zoomed 32x32 frame");
            Check(GetPixel(dst, 538, 218) == 0, "direct frame stays inside projected bounds");
            Call("PresentMapAnimation", dst, 1002, 314, 64, 64, src, 0, 0, 0x00660046u); // SRCINVERT
            Check(GetPixel(dst, 506, 186) == 0, "native raster operation is preserved rather than replaced with color-key transparency");
            Call("PresentMapAnimation", dst, 1130, 314, 64, 64, src, 0, 0, 0x00CC0020u);
            Check(GetPixel(dst, 585, 186) == 0x000000FF && GetPixel(dst, 586, 186) == 0,
                "partially visible frame clips at the map edge without overwriting sidebar");
            GraphicsBridge.SetZoom(0.75f);
            Check(Same(GraphicsBridge.MapAnimationRectangle(R(10, 58, 778, 741)), R(10, 58, 586, 570)),
                "fractional zoom uses exact expanded extent including rounded source height");
        }
        finally
        {
            SelectObject(src, oldSrc); SelectObject(dst, oldDst); DeleteObject(srcBmp); DeleteObject(dstBmp);
            DeleteDC(src); DeleteDC(dst); Marshal.FreeHGlobal(bmi);
        }
    }

    public static int Main()
    {
        try
        {
            TestTickInvalidation(); TestAnimationRetention(); TestMapInteractions(); TestDirtyPixels(); TestDirectBlit();
            Console.WriteLine("All " + passed + " map animation checks passed."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { GraphicsBridge.SetZoom(1.0f); }
    }
}
