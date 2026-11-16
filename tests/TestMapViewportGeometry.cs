using System;
using Imp2Net.Graphics;

internal static class TestMapViewportGeometry
{
    private static int checks;
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    private static void Near(double expected, double actual, string message)
    { Check(Math.Abs(expected - actual) < 0.00000001, message + ": " + actual); }
    private static void Rect(PixelRect actual, int left, int top, int right, int bottom, string message)
    { Check(actual.Left == left && actual.Top == top && actual.Right == right && actual.Bottom == bottom, message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (ArgumentException) { checks++; return; }
        throw new Exception("Expected rejection: " + message);
    }

    private static void VerifyDisplay(int width, int height, double scale, int mapWidth, int mapHeight, int maxCameraRow)
    {
        MapViewportGeometry geometry = new MapViewportGeometry(width, height, scale, 17);
        Check(geometry.NativeMapWidth == mapWidth && geometry.NativeMapHeight == mapHeight, "Viewport extent");
        Check(geometry.GuardedBufferWidth == mapWidth + 128 && geometry.GuardedBufferHeight == mapHeight + 128, "Guard allocation");
        Check(geometry.MaximumRowAlignedCameraRow == maxCameraRow, "Bottom camera boundary");
        Rect(geometry.BufferSourceBounds, 64, 64, 64 + mapWidth, 64 + mapHeight, "Guard source rectangle");
        Rect(geometry.AnimationRetentionBounds, -64, -64, mapWidth + 64, mapHeight + 64, "Animation retention");

        // A physical landmark and its sprite footprint remain the same size even
        // when UI scale changes. The far right/bottom point exceeds legacy bounds.
        foreach (GeometryPoint local in new GeometryPoint[] {
            new GeometryPoint(0,0), new GeometryPoint(63,127),
            new GeometryPoint(mapWidth-1,mapHeight-1), new GeometryPoint(575,511) })
        {
            GeometryPoint physical = geometry.MapToPhysical(local.X, local.Y);
            GeometryPoint restored;
            Check(geometry.TryPhysicalToMap(physical.X, physical.Y, out restored), "Visible landmark hit");
            Near(local.X, restored.X, "Map inverse X"); Near(local.Y, restored.Y, "Map inverse Y");
            GeometryPoint next = geometry.MapToPhysical(local.X + 64, local.Y + 64);
            Near(64, next.X - physical.X, "Physical tile pitch X");
            Near(64, next.Y - physical.Y, "Physical tile pitch Y");
        }
        GeometryPoint unused;
        Check(!geometry.TryPhysicalToMap(geometry.MapPhysicalBounds.Right, geometry.MapPhysicalBounds.Top, out unused), "Right edge exclusive");
        Check(!geometry.TryPhysicalToMap(geometry.MapPhysicalBounds.Left, geometry.MapPhysicalBounds.Bottom, out unused), "Bottom edge exclusive");
        Check(!geometry.TryPhysicalToMap(geometry.MapPhysicalBounds.Left - 1, geometry.MapPhysicalBounds.Top, out unused), "Chrome not map");
        Rect(geometry.MapDirtyBounds(new PixelRect(-100, -30, mapWidth + 500, mapHeight + 10)),
            geometry.MapPhysicalBounds.Left, geometry.MapPhysicalBounds.Top,
            geometry.MapPhysicalBounds.Right, geometry.MapPhysicalBounds.Bottom, "Native dirty clipping");
        Rect(geometry.MapDirtyBounds(new PixelRect(mapWidth+1, 20, mapWidth+50, 30)),
            geometry.MapPhysicalBounds.Right, geometry.MapPhysicalBounds.Top+20,
            geometry.MapPhysicalBounds.Right, geometry.MapPhysicalBounds.Top+30, "Offscreen dirty empty");

        UiPaneTransform sidebar = geometry.CreateUiPane(new PixelRect(586,58,800,570), HorizontalUiAnchor.Right, VerticalUiAnchor.Top);
        GeometryPoint control = sidebar.ToPhysical(797, 128);
        Near(width - 3 * scale, control.X, "UI right anchoring");
        Near(128 * scale, control.Y, "UI top anchoring");
        GeometryPoint logical = sidebar.ToLogical(control.X, control.Y);
        Near(797, logical.X, "UI inverse X"); Near(128, logical.Y, "UI inverse Y");
        Check(sidebar.HitTest(control.X, control.Y), "UI control hit");
        GeometryPoint rightEdge = sidebar.ToPhysical(800, 128);
        Check(!sidebar.HitTest(rightEdge.X, rightEdge.Y), "UI right edge exclusive");
        Check(sidebar.Generation == geometry.Generation, "Frame generation travels with UI");
        UiPaneTransform bottom = geometry.CreateUiPane(new PixelRect(0,570,800,600), HorizontalUiAnchor.Left, VerticalUiAnchor.Bottom);
        GeometryPoint last = bottom.ToPhysical(300, 600);
        Near(height, last.Y, "UI bottom anchoring");
    }

    public static int Main()
    {
        try
        {
            VerifyDisplay(800,600,1,576,512,52);
            VerifyDisplay(1920,1080,1,1696,992,44);
            VerifyDisplay(3840,2160,1,3616,2072,27);
            VerifyDisplay(1920,1080,1.5,1584,948,45);
            VerifyDisplay(3840,2160,3,3168,1896,30);

            MapViewportGeometry native = new MapViewportGeometry(800,600,1,0);
            Rect(native.MapPhysicalBounds,10,58,586,570,"Original placement");
            MapViewportGeometry fractional = new MapViewportGeometry(1920,1080,1.25,18);
            Rect(fractional.MapPhysicalBounds,13,73,1652,1042,"Deterministic half-pixel edges");
            UiPaneTransform pane = fractional.CreateUiPane(new PixelRect(586,58,800,570),HorizontalUiAnchor.Right,VerticalUiAnchor.Top);
            PixelRect dirty = pane.MapDirtyBounds(new PixelRect(587,59,588,60));
            // x:1653.75..1655, y:73.75..75; retain both touched columns.
            Rect(dirty,1653,73,1655,75,"Fractional UI dirty bounds round outward");
            PixelRect empty = pane.MapDirtyBounds(new PixelRect(587,59,587,60));
            Check(empty.Width == 0,"Empty dirty region stays empty at fractional scale");
            PixelRect outside = pane.MapDirtyBounds(new PixelRect(0,0,20,10));
            Check(outside.Width == 0 && outside.Height == 0,"UI dirty bounds clipped to owning pane");
            MapViewportGeometry next = new MapViewportGeometry(1920,1080,1.5,19);
            Check(fractional.Generation==18 && next.Generation==19 && fractional.UiScale==1.25,"Old snapshot survives replacement");

            Reject(delegate { new MapViewportGeometry(800,600,double.NaN,0); },"NaN scale");
            Reject(delegate { new MapViewportGeometry(800,600,double.PositiveInfinity,0); },"Infinite scale");
            Reject(delegate { new MapViewportGeometry(800,600,0,0); },"Zero scale");
            Reject(delegate { new MapViewportGeometry(800,600,2,0); },"UI cannot fit");
            Reject(delegate { new MapViewportGeometry(-1,600,1,0); },"Negative display");
            Reject(delegate { new MapViewportGeometry(40000,2160,1,0); },"Native overflow");
            Reject(delegate { new MapViewportGeometry(800,600,1,-1); },"Negative generation");
            Reject(delegate { native.CreateUiPane(new PixelRect(0,0,801,20),HorizontalUiAnchor.Left,VerticalUiAnchor.Top); },"Outside logical UI");
            Reject(delegate { native.CreateUiPane(new PixelRect(0,0,20,20),(HorizontalUiAnchor)7,VerticalUiAnchor.Top); },"Unknown anchor");
            Console.WriteLine("PASS: {0} map/UI geometry checks; no game process or native hooks used.",checks);
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
