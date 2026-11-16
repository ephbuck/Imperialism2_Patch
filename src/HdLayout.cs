using System;
using System.Globalization;

namespace Imp2Net.Graphics
{
    // One immutable snapshot for composition, input and native viewport size.
    // All pane edges are quantized once; neither map pixels nor the tile decoder
    // are scaled. Legacy coordinates remain the game's private 800x600 canvas.
    public sealed class HdLayout
    {
        public readonly int Width, Height;
        public readonly double Scale, RequestedScale;
        public readonly bool ScaleLimited;
        public readonly long Generation;
        public readonly PixelRect Map, Sidebar, SidebarMain, SidebarDataFoot, SidebarBottomBorder, SidebarFooter, SidebarLeftFoot, SidebarRightFoot, Top, Bottom, Menu;
        public readonly int SidebarWidth;

        public HdLayout(int width, int height, double uiScale, double dpiScale, double sidebarFraction, long generation)
        {
            if (width < 800 || height < 600 || width > 3840 || height > 2160)
                throw new ArgumentOutOfRangeException("width", "Supported client extent is 800x600 through 3840x2160.");
            if (!Finite(uiScale) || uiScale < .5 || uiScale > 4 || !Finite(dpiScale) || dpiScale < .5 || dpiScale > 4)
                throw new ArgumentOutOfRangeException("uiScale");
            if (!Finite(sidebarFraction) || sidebarFraction < .12 || sidebarFraction > .4 || generation < 0)
                throw new ArgumentOutOfRangeException("sidebarFraction");
            Width = width; Height = height; Generation = generation;
            // 20% is a target, subject to the complete 600px column fitting and
            // retaining at least the original map area. Accessibility settings
            // may consume more width; they never clip the column's controls.
            RequestedScale = Math.Max(1, width * sidebarFraction / 214.0) * uiScale * dpiScale;
            double fit = Math.Min(height / 600.0, Math.Min((width - 576) / 224.0, (height - 512) / 88.0));
            Scale = Math.Max(1, Math.Min(RequestedScale, fit));
            ScaleLimited = Scale + .000001 < RequestedScale;
            SidebarWidth = Edge(214);
            int left = Edge(10), top = Edge(58), bottom = height - Edge(30), right = width - SidebarWidth;
            Map = new PixelRect(left, top, right, bottom);
            // Unit information reaches the bottom of the original canvas. Keep
            // all 600 content rows together; only the two decorative feet move.
            Sidebar = new PixelRect(right, 0, width, Edge(600));
            SidebarFooter = new PixelRect(right,height-Edge(60),width,height);
            SidebarLeftFoot = new PixelRect(right,SidebarFooter.Top,right+Edge(34),height);
            SidebarRightFoot = new PixelRect(width-Edge(36),SidebarFooter.Top,width,height);
            int mainBottom=Math.Min(Edge(540),SidebarFooter.Top);
            SidebarMain=new PixelRect(right,0,width,mainBottom);
            SidebarDataFoot=new PixelRect(SidebarLeftFoot.Right,mainBottom,SidebarRightFoot.Left,Edge(592));
            SidebarBottomBorder=new PixelRect(SidebarLeftFoot.Right,height-Edge(8),SidebarRightFoot.Left,height);
            int groupWidth = Edge(586);
            int groupLeft = (left + right - groupWidth) / 2;
            // At legacy size retain the exact original placement.
            if (width == 800 && height == 600) groupLeft = 0;
            Top = new PixelRect(groupLeft, 0, groupLeft + groupWidth, top);
            Bottom = new PixelRect(groupLeft, bottom, groupLeft + groupWidth, height);
            double menuScale = Math.Min(Scale, Math.Min(width / 800.0, height / 600.0));
            int mw = Round(800 * menuScale), mh = Round(600 * menuScale);
            Menu = new PixelRect((width - mw) / 2, (height - mh) / 2, (width - mw) / 2 + mw, (height - mh) / 2 + mh);
        }
        static bool Finite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }
        static int Round(double x) { return checked((int)Math.Round(x, MidpointRounding.AwayFromZero)); }
        public int Edge(int logical) { return Round(logical * Scale); }
        static GeometryPoint Inverse(PixelRect dest, PixelRect source, double x, double y)
        { return new GeometryPoint(source.Left + (x-dest.Left) * source.Width / dest.Width,
            source.Top + (y-dest.Top) * source.Height / dest.Height); }
        static GeometryPoint Forward(PixelRect dest, PixelRect source, double x, double y)
        { return new GeometryPoint(dest.Left + (x-source.Left) * dest.Width / source.Width,
            dest.Top + (y-source.Top) * dest.Height / source.Height); }
        static readonly PixelRect TopSource = new PixelRect(0,0,586,58), BottomSource = new PixelRect(0,570,586,600),
            SidebarSource = new PixelRect(586,0,800,540), DataFootSource = new PixelRect(620,540,764,592), BorderSource = new PixelRect(620,592,764,600),
            LeftFootSource = new PixelRect(586,540,620,600), RightFootSource = new PixelRect(764,540,800,600), MenuSource = new PixelRect(0,0,800,600);
        public bool TryUiToLogical(double x, double y, bool map, out GeometryPoint point)
        {
            if (!map) { point = Inverse(Menu,MenuSource,x,y); return Menu.Contains(x,y); }
            if (SidebarLeftFoot.Contains(x,y)) { point=Inverse(SidebarLeftFoot,LeftFootSource,x,y);return true; }
            if (SidebarRightFoot.Contains(x,y)) { point=Inverse(SidebarRightFoot,RightFootSource,x,y);return true; }
            if (SidebarBottomBorder.Contains(x,y)) { point=Inverse(SidebarBottomBorder,BorderSource,x,y);return true; }
            if (SidebarDataFoot.Contains(x,y)) { point=Inverse(SidebarDataFoot,DataFootSource,x,y);return true; }
            if (SidebarMain.Contains(x,y)) { point=Inverse(SidebarMain,SidebarSource,x,y);return true; }
            if (Top.Contains(x,y)) { point=Inverse(Top,TopSource,x,y); return true; }
            if (Bottom.Contains(x,y)) { point=Inverse(Bottom,BottomSource,x,y); return true; }
            // Decorative filler has no controls and must never hit the map or
            // a legacy control at an unrelated coordinate.
            point = new GeometryPoint(-1000,-1000); return false;
        }
        public GeometryPoint UiToPhysical(double x, double y, bool map)
        {
            if (!map) return Forward(Menu,MenuSource,x,y);
            if (x >= 586) {
                if(y>=540 && x<620) return Forward(SidebarLeftFoot,LeftFootSource,x,y);
                if(y>=540 && x>=764) return Forward(SidebarRightFoot,RightFootSource,x,y);
                if(y>=592) return Forward(SidebarBottomBorder,BorderSource,x,y);
                if(y>=540) return Forward(SidebarDataFoot,DataFootSource,x,y);
                return Forward(SidebarMain,SidebarSource,x,y);
            }
            if (y >= 570) return Forward(Bottom,BottomSource,x,y);
            return Forward(Top,TopSource,x,y);
        }
        public override string ToString()
        { return String.Format(CultureInfo.InvariantCulture,"{0}x{1}, map={2}x{3}, sidebar={4} ({5:P1}), UI={6:F3}{7}",
            Width,Height,Map.Width,Map.Height,SidebarWidth,SidebarWidth/(double)Width,Scale,ScaleLimited?" (limited to fit)":""); }
    }
}
