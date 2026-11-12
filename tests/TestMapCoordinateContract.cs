using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Imp2Net;

// Headless x86 contract tests. The delegates below stand in for native pane
// localization and ScreenToTile, not for the production zoom/presentation hooks.
// Native hit arithmetic is from 0x0055A5A0..0x0055A6A0 in the supported 1.03 EXE.
// These tests do not execute that EXE or establish gameplay acceptance.
internal static class TestMapCoordinateContract
{
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    const int MapLeft = 10, MapTop = 58, MapWidth = 576, MapHeight = 512;
    const int Margin = 64, Columns = 108;
    static int passed, diagnostics;
    static int cameraX = 104 * 64 + 11, cameraY = 20 * 64 + 17;
    static POINT routedCanvas, nativePoint;
    static readonly GraphicsBridge.WndProcDelegate route = CapturePaneInput;

    static void Set(string name, object value) { typeof(GraphicsBridge).GetField(name, Hidden).SetValue(null, value); }
    static object Call(string name, params object[] args) { return typeof(GraphicsBridge).GetMethod(name, Hidden).Invoke(null, args); }
    static void Geometry(string name, int value) { typeof(GraphicsBridge).GetProperty(name).GetSetMethod(true).Invoke(null, new object[] { value }); }
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        passed++;
        Console.WriteLine("PASS " + message);
    }
    static RECT Rect(int left, int top, int right, int bottom)
    { return new RECT { Left = left, Top = top, Right = right, Bottom = bottom }; }

    static IntPtr CapturePaneInput(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        routedCanvas = new POINT { X = (short)(lParam.ToInt32() & 0xffff), Y = (short)(lParam.ToInt32() >> 16) };
        return IntPtr.Zero;
    }

    // Native ScreenToTile consumes MAP-LOCAL input, adds the camera, handles
    // odd-row staggering, then wraps the column ONCE and clamps the row.
    // x87 helper 0x006816B0 truncates toward zero; C# integer division agrees.
    // The third output is a 1..4 region classification, not a boolean validity flag.
    static void DecodeNativePoint(int localX, int localY, out short column, out short row, out short region)
    {
        int worldY = localY + cameraY;
        int worldX = localX + cameraX;
        int r = worldY / 64;
        bool odd = (r & 1) != 0;
        int c = (worldX + (odd ? 32 : 0)) / 64 - (odd ? 1 : 0);
        // 0x00408279 -> 0x00550340: this is not an arbitrary modulo operation.
        if (c < 0) c += Columns; else if (c >= Columns) c -= Columns;
        column = (short)c;
        row = (short)Math.Max(0, Math.Min(59, r));
        odd = (row & 1) != 0; // The region calculation reloads the normalized row.
        int xRemainder = (worldX + (odd ? 32 : 0)) % 64 - (odd ? 1 : 0);
        int yRemainder = worldY % 64;
        region = (short)(xRemainder < 20 ? (yRemainder >= 20 && yRemainder <= 44 ? 1 : 2) : (yRemainder >= 32 ? 4 : 3));
    }

    static void NativeLookup(IntPtr view, IntPtr point, IntPtr columnOut, IntPtr rowOut, IntPtr regionOut)
    {
        nativePoint = (POINT)Marshal.PtrToStructure(point, typeof(POINT));
        short column, row, region;
        DecodeNativePoint(nativePoint.X, nativePoint.Y, out column, out row, out region);
        Marshal.WriteInt16(columnOut, column);
        Marshal.WriteInt16(rowOut, row);
        Marshal.WriteInt16(regionOut, region);
    }

    sealed class PixMap : IDisposable
    {
        public IntPtr Map, Dib, Bmi, Bits;
        readonly int stride;
        public PixMap(int width, int height, bool encodeWorld)
        {
            stride = (width * 2 + 3) & ~3;
            Map = Alloc(36); Dib = Alloc(32); Bmi = Alloc(40); Bits = Alloc(stride * height);
            Marshal.WriteIntPtr(Map, 28, Dib);
            Marshal.WriteIntPtr(Dib, 12, Bits); Marshal.WriteIntPtr(Dib, 16, Bmi);
            Marshal.WriteInt32(Bmi, 40); Marshal.WriteInt32(Bmi, 4, width); Marshal.WriteInt32(Bmi, 8, -height);
            Marshal.WriteInt16(Bmi, 12, 1); Marshal.WriteInt16(Bmi, 14, 16);
            if (encodeWorld)
            {
                short[] pixels = new short[stride / 2 * height];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    short column, row, region;
                    DecodeNativePoint(x - Margin, y - Margin, out column, out row, out region);
                    pixels[y * (stride / 2) + x] = (short)(row * Columns + column);
                }
                Marshal.Copy(pixels, 0, Bits, pixels.Length);
            }
        }
        static IntPtr Alloc(int size)
        {
            IntPtr pointer = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, pointer, size);
            return pointer;
        }
        public short Pixel(int canvasX, int canvasY) { return Marshal.ReadInt16(Bits, canvasY * stride + canvasX * 2); }
        public void Dispose()
        { Marshal.FreeHGlobal(Map); Marshal.FreeHGlobal(Dib); Marshal.FreeHGlobal(Bmi); Marshal.FreeHGlobal(Bits); }
    }

    static void RunScene(float zoom, int sourceX, int sourceY)
    {
        GraphicsBridge.SetZoom(zoom);
        GraphicsBridge.CurrentZoomFilter = ZoomFilter.Nearest;
        int extentX = (int)Math.Ceiling(MapWidth / (double)zoom);
        int extentY = (int)Math.Ceiling(MapHeight / (double)zoom);
        IntPtr rectangles = Marshal.AllocHGlobal(32), point = Marshal.AllocHGlobal(8), outputs = Marshal.AllocHGlobal(12);
        try
        {
            using (PixMap source = new PixMap(sourceX + extentX, sourceY + extentY, true))
            using (PixMap canvas = new PixMap(800, 600, false))
            {
                Geometry("TargetW", 0); // Pixel sampling needs no actual game window.
                Marshal.StructureToPtr(Rect(sourceX, sourceY, sourceX + extentX, sourceY + extentY), rectangles, false);
                GraphicsBridge.DownsampleMapBuffer(source.Map, canvas.Map, rectangles, IntPtr.Add(rectangles, 16));
                Check(GraphicsBridge.LastSx0 == sourceX && GraphicsBridge.LastSy0 == sourceY,
                    "real sampler records source origin at zoom=" + zoom + " origin=" + sourceX + "," + sourceY);
                double scaleX = GraphicsBridge.LastScaleX, scaleY = GraphicsBridge.LastScaleY;
                Check(Math.Abs(scaleX - extentX / 576.0) < 1e-10 && Math.Abs(scaleY - extentY / 512.0) < 1e-10,
                    "real sampler records exact rounded source extents at zoom=" + zoom);

                // World tile centers: both row parities, and columns on both sides
                // of the seam (unwrapped 108/109 become native columns 0/1).
                int[] worldColumns = { 105, 108, 109, 111 };
                int[] worldRows = { 21, 22, 23, 24 };
                foreach (bool stretch in new bool[] { false, true })
                {
                    int width = stretch ? 3840 : 2880, height = 2160, offsetX = stretch ? 0 : 480;
                    Geometry("TargetW", width); Geometry("TargetH", height); Geometry("DstX", offsetX); Geometry("DstY", 0);
                    for (int i = 0; i < worldColumns.Length; i++)
                    {
                        int worldColumn = worldColumns[i], worldRow = worldRows[i];
                        int nativeX = worldColumn * 64 + ((worldRow & 1) != 0 ? 32 : 0) + 32 - cameraX;
                        int nativeY = worldRow * 64 + 32 - cameraY;
                        double canvasX = MapLeft + (nativeX + Margin - sourceX) / scaleX;
                        double canvasY = MapTop + (nativeY + Margin - sourceY) / scaleY;
                        int physicalX = offsetX + (int)Math.Round(canvasX * width / 800.0);
                        int physicalY = (int)Math.Round(canvasY * height / 600.0);
                        Call("ViewWndProc", (IntPtr)123, 0x0201u, IntPtr.Zero, (IntPtr)((physicalY << 16) | (physicalX & 0xffff)));

                        // Explicit native pane step: routing selected TMapDialog,
                        // which localizes the canvas point exactly once.
                        POINT mapLocal = new POINT { X = routedCanvas.X - MapLeft, Y = routedCanvas.Y - MapTop };
                        Marshal.StructureToPtr(mapLocal, point, false);
                        for (int slot = 0; slot < 3; slot++) Marshal.WriteInt32(outputs, slot * 4, 0x6A6A6A6A);
                        Call("MyMapScreenToTileHook", (IntPtr)456, point, outputs, IntPtr.Add(outputs, 4), IntPtr.Add(outputs, 8));
                        int column = Marshal.ReadInt16(outputs), row = Marshal.ReadInt16(outputs, 4);
                        string context = "zoom=" + zoom + " " + (stretch ? "Stretch" : "Fit") + " tile=" + worldColumn % 108 + "," + worldRow + " source=" + sourceX + "," + sourceY;
                        Check(column == worldColumn % Columns && row == worldRow, "physical click returns intended native tile: " + context);
                        Check(canvas.Pixel(routedCanvas.X, routedCanvas.Y) == row * Columns + column,
                            "real downsampled terrain pixel agrees with clicked tile: " + context);
                        POINT after = (POINT)Marshal.PtrToStructure(point, typeof(POINT));
                        Check(after.X == mapLocal.X && after.Y == mapLocal.Y, "tile hook preserves caller map-local point: " + context);
                        Check(Marshal.ReadInt16(outputs, 2) == 0x6A6A && Marshal.ReadInt16(outputs, 6) == 0x6A6A && Marshal.ReadInt16(outputs, 10) == 0x6A6A,
                            "native fixture writes 16-bit column, row and region outputs: " + context);
                    }
                }

                // Independently compare the animation helper with the source grid
                // actually established by the sampler. Offset-source differences
                // are reported as known diagnostics rather than hidden by a pass.
                const int anchorX = 200, anchorY = 180;
                RECT animation = GraphicsBridge.MapAnimationRectangle(Rect(MapLeft + anchorX, MapTop + anchorY,
                    MapLeft + anchorX + 64, MapTop + anchorY + 64));
                int terrainLeft = MapLeft + (int)Math.Floor((anchorX + Margin - sourceX) / scaleX);
                int terrainTop = MapTop + (int)Math.Floor((anchorY + Margin - sourceY) / scaleY);
                if (sourceX == Margin && sourceY == Margin)
                    Check(animation.Left == terrainLeft && animation.Top == terrainTop,
                        "animation helper shares canonical terrain anchor at zoom=" + zoom);
                else if (animation.Left != terrainLeft || animation.Top != terrainTop)
                {
                    diagnostics++;
                    Console.WriteLine("DIAGNOSTIC zoom=" + zoom + " source=(" + sourceX + "," + sourceY + ") terrain anchor=(" + terrainLeft + "," + terrainTop
                        + ") animation anchor=(" + animation.Left + "," + animation.Top + "): zoom-only animation geometry omits the sampled source offset.");
                }
                else Console.WriteLine("INFO offset-source animation anchor agrees with the recorded terrain grid.");
            }
        }
        finally { Marshal.FreeHGlobal(rectangles); Marshal.FreeHGlobal(point); Marshal.FreeHGlobal(outputs); }
    }

    static void TestNativeRegionContract()
    {
        int oldX = cameraX, oldY = cameraY;
        cameraX = cameraY = 0;
        try
        {
            int[] xs = { 10, 10, 32, 32 }, ys = { 32, 10, 10, 40 };
            for (int i = 0; i < 4; i++)
            {
                short decodedColumn, decodedRow, region;
                DecodeNativePoint(xs[i], ys[i], out decodedColumn, out decodedRow, out region);
                Check(region == i + 1, "native fixture preserves region classification " + (i + 1) + " rather than treating it as bool");
            }
            short col, row, mask;
            DecodeNativePoint(-1, 32, out col, out row, out mask);
            Check(col == 0, "native x87 truncation maps X=-1 to column zero rather than floor-based column 107");
            DecodeNativePoint(-65, 32, out col, out row, out mask);
            Check(col == 107, "one negative-column wrap crosses the cylinder seam");
            DecodeNativePoint(32, -1, out col, out row, out mask);
            Check(row == 0, "native truncation preserves the top-edge Y=-1 row-zero behavior");
            DecodeNativePoint(32, -65, out col, out row, out mask);
            Check(row == 0, "native normalization clamps a negative decoded row to zero");
            DecodeNativePoint(32, 60 * 64, out col, out row, out mask);
            Check(row == 59, "native normalization clamps past-bottom decoded rows to 59");
            DecodeNativePoint(-109 * 64, 32, out col, out row, out mask);
            Check(col == -1, "native one-wrap limitation remains visible for X beyond a full negative circumference");
            DecodeNativePoint(216 * 64, 32, out col, out row, out mask);
            Check(col == 108, "native one-wrap limitation remains visible for X beyond two circumferences");
        }
        finally { cameraX = oldX; cameraY = oldY; }
    }

    public static int Main()
    {
        try
        {
            Set("s_animationLog", false); Set("s_zoomEnabled", true); Set("s_isInsideMapDraw", false);
            Set("s_cachedCanvasDC", IntPtr.Zero); Set("s_viewHwnd", (IntPtr)123);
            Set("s_origViewWndProc", Marshal.GetFunctionPointerForDelegate(route));
            Set("s_origMapScreenToTile", new GraphicsBridge.MapScreenToTileDelegate(NativeLookup));
            TestNativeRegionContract();
            foreach (float zoom in new float[] { 0.75f, 0.5f, 0.25f }) RunScene(zoom, Margin, Margin);
            foreach (float zoom in new float[] { 0.75f, 0.5f, 0.25f }) RunScene(zoom, 74, 122);
            Console.WriteLine("All " + passed + " map coordinate contract checks passed; " + diagnostics + " source-offset animation discrepancies reported.");
            Console.WriteLine("Native decoder and pane localization are controlled fixtures; native callback ABI and actual gameplay remain outside this suite.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            Set("s_viewHwnd", IntPtr.Zero); Set("s_origViewWndProc", IntPtr.Zero); GraphicsBridge.SetZoom(1.0f);
            GC.KeepAlive(route);
        }
    }
}
