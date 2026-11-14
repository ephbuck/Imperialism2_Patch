using System;

namespace Imp2Net.Graphics
{
    // Rectangles use half-open right/bottom edges, as Win32 RECT does.
    public struct PixelRect
    {
        public readonly int Left, Top, Right, Bottom;
        public PixelRect(int left, int top, int right, int bottom)
        {
            if (right < left || bottom < top) throw new ArgumentException("Inverted rectangle.");
            Left = left; Top = top; Right = right; Bottom = bottom;
        }
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public bool Contains(double x, double y)
        { return x >= Left && y >= Top && x < Right && y < Bottom; }
    }

    public struct GeometryPoint
    {
        public readonly double X, Y;
        public GeometryPoint(double x, double y) { X = x; Y = y; }
    }

    public enum HorizontalUiAnchor { Left, Right }
    public enum VerticalUiAnchor { Top, Bottom }

    // Transform one existing logical UI pane; never apply this to map pixels.
    public sealed class UiPaneTransform
    {
        public readonly PixelRect LogicalBounds;
        public readonly double PhysicalLeft, PhysicalTop, Scale;
        public readonly long Generation;
        internal UiPaneTransform(PixelRect logical, double x, double y, double scale, long generation)
        { LogicalBounds = logical; PhysicalLeft = x; PhysicalTop = y; Scale = scale; Generation = generation; }

        public GeometryPoint ToPhysical(double logicalX, double logicalY)
        {
            return new GeometryPoint(PhysicalLeft + (logicalX - LogicalBounds.Left) * Scale,
                PhysicalTop + (logicalY - LogicalBounds.Top) * Scale);
        }
        public GeometryPoint ToLogical(double physicalX, double physicalY)
        {
            return new GeometryPoint(LogicalBounds.Left + (physicalX - PhysicalLeft) / Scale,
                LogicalBounds.Top + (physicalY - PhysicalTop) / Scale);
        }
        public bool HitTest(double physicalX, double physicalY)
        {
            GeometryPoint point = ToLogical(physicalX, physicalY);
            return LogicalBounds.Contains(point.X, point.Y);
        }
        public PixelRect MapDirtyBounds(PixelRect logical)
        {
            int left = Math.Max(LogicalBounds.Left, Math.Min(LogicalBounds.Right, logical.Left));
            int top = Math.Max(LogicalBounds.Top, Math.Min(LogicalBounds.Bottom, logical.Top));
            int right = Math.Max(left, Math.Min(LogicalBounds.Right, logical.Right));
            int bottom = Math.Max(top, Math.Min(LogicalBounds.Bottom, logical.Bottom));
            GeometryPoint first = ToPhysical(left, top);
            GeometryPoint last = ToPhysical(right, bottom);
            int x = checked((int)Math.Floor(first.X)), y = checked((int)Math.Floor(first.Y));
            return new PixelRect(x, y, right == left ? x : checked((int)Math.Ceiling(last.X)),
                bottom == top ? y : checked((int)Math.Ceiling(last.Y)));
        }
    }

    // Publish one immutable instance together with its completed frame. This class
    // computes geometry only: it does not change a native pane, buffer or camera.
    public sealed class MapViewportGeometry
    {
        public const int GuardPixels = 64;
        public const int WorldWidthPixels = 6912;
        public const int WorldHeightPixels = 3840;
        public readonly int DisplayWidth, DisplayHeight;
        public readonly double UiScale;
        public readonly long Generation;
        public readonly PixelRect MapPhysicalBounds;

        public MapViewportGeometry(int displayWidth, int displayHeight, double uiScale, long generation)
        {
            if (double.IsNaN(uiScale) || double.IsInfinity(uiScale) || uiScale <= 0)
                throw new ArgumentOutOfRangeException("uiScale");
            if (displayWidth < 800 * uiScale || displayHeight < 600 * uiScale)
                throw new ArgumentException("The original UI must fit at the selected scale.");
            if (displayWidth > 32767 - 2 * GuardPixels || displayHeight > 32767 - 2 * GuardPixels)
                throw new ArgumentOutOfRangeException("displayWidth", "Native signed coordinate range exceeded.");
            if (generation < 0) throw new ArgumentOutOfRangeException("generation");
            DisplayWidth = displayWidth; DisplayHeight = displayHeight;
            UiScale = uiScale; Generation = generation;
            MapPhysicalBounds = new PixelRect(RoundEdge(10 * uiScale), RoundEdge(58 * uiScale),
                displayWidth - RoundEdge(214 * uiScale), displayHeight - RoundEdge(30 * uiScale));
        }

        private static int RoundEdge(double value)
        { return checked((int)Math.Round(value, MidpointRounding.AwayFromZero)); }
        public int NativeMapWidth { get { return MapPhysicalBounds.Width; } }
        public int NativeMapHeight { get { return MapPhysicalBounds.Height; } }
        public int GuardedBufferWidth { get { return NativeMapWidth + 2 * GuardPixels; } }
        public int GuardedBufferHeight { get { return NativeMapHeight + 2 * GuardPixels; } }
        public int MaximumRowAlignedCameraRow
        { get { return Math.Max(0, WorldHeightPixels - NativeMapHeight) / 64; } }
        public PixelRect NativeMapBounds { get { return new PixelRect(0, 0, NativeMapWidth, NativeMapHeight); } }
        public PixelRect BufferSourceBounds
        { get { return new PixelRect(GuardPixels, GuardPixels, GuardPixels + NativeMapWidth, GuardPixels + NativeMapHeight); } }
        public PixelRect AnimationRetentionBounds
        { get { return new PixelRect(-GuardPixels, -GuardPixels, NativeMapWidth + GuardPixels, NativeMapHeight + GuardPixels); } }

        public GeometryPoint MapToPhysical(double localX, double localY)
        { return new GeometryPoint(MapPhysicalBounds.Left + localX, MapPhysicalBounds.Top + localY); }
        public GeometryPoint PhysicalToMap(double physicalX, double physicalY)
        { return new GeometryPoint(physicalX - MapPhysicalBounds.Left, physicalY - MapPhysicalBounds.Top); }
        public bool TryPhysicalToMap(double x, double y, out GeometryPoint local)
        {
            local = PhysicalToMap(x, y);
            return MapPhysicalBounds.Contains(x, y);
        }
        public PixelRect MapDirtyBounds(PixelRect nativeLocal)
        {
            // Clip in native map space before translating. Empty input stays empty.
            int left = Math.Max(0, Math.Min(NativeMapWidth, nativeLocal.Left));
            int right = Math.Max(left, Math.Min(NativeMapWidth, nativeLocal.Right));
            int top = Math.Max(0, Math.Min(NativeMapHeight, nativeLocal.Top));
            int bottom = Math.Max(top, Math.Min(NativeMapHeight, nativeLocal.Bottom));
            return new PixelRect(left + MapPhysicalBounds.Left, top + MapPhysicalBounds.Top,
                right + MapPhysicalBounds.Left, bottom + MapPhysicalBounds.Top);
        }
        public UiPaneTransform CreateUiPane(PixelRect logicalBounds, HorizontalUiAnchor horizontal, VerticalUiAnchor vertical)
        {
            if (logicalBounds.Left < 0 || logicalBounds.Top < 0 || logicalBounds.Right > 800 || logicalBounds.Bottom > 600)
                throw new ArgumentException("UI pane must belong to the original logical canvas.");
            if (horizontal != HorizontalUiAnchor.Left && horizontal != HorizontalUiAnchor.Right)
                throw new ArgumentOutOfRangeException("horizontal");
            if (vertical != VerticalUiAnchor.Top && vertical != VerticalUiAnchor.Bottom)
                throw new ArgumentOutOfRangeException("vertical");
            double x = horizontal == HorizontalUiAnchor.Left ? logicalBounds.Left * UiScale :
                DisplayWidth - (800 - logicalBounds.Left) * UiScale;
            double y = vertical == VerticalUiAnchor.Top ? logicalBounds.Top * UiScale :
                DisplayHeight - (600 - logicalBounds.Top) * UiScale;
            return new UiPaneTransform(logicalBounds, x, y, UiScale, Generation);
        }
    }
}
