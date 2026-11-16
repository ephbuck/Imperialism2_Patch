using System;
using Imp2Net.Graphics;
class TestHdLayout
{
    static int checks;
    static void Check(bool condition,string label) { checks++; if(!condition) throw new Exception(label); }
    static void Near(double a,double b,string label) { Check(Math.Abs(a-b)<.000001,label); }
    static void RoundTrip(HdLayout g,PixelRect source,bool map)
    {
        foreach(double x in new double[]{source.Left+.5,(source.Left+source.Right)/2.0,source.Right-.5})
        foreach(double y in new double[]{source.Top+.5,(source.Top+source.Bottom)/2.0,source.Bottom-.5})
        {
            GeometryPoint p=g.UiToPhysical(x,y,map),q;
            Check(g.TryUiToLogical(p.X,p.Y,map,out q),"pane hit");
            Near(x,q.X,"inverse X"); Near(y,q.Y,"inverse Y");
        }
    }
    static void Main()
    {
        int[,] resolutions={{800,600},{1280,900},{1920,1080},{2560,1440},{3840,2160},{3440,1440}};
        for(int r=0;r<resolutions.GetLength(0);r++)
        foreach(double dpi in new double[]{1,1.5,2})
        foreach(double ui in new double[]{.75,1,1.25,1.5,2})
        {
            HdLayout g=new HdLayout(resolutions[r,0],resolutions[r,1],ui,dpi,.2,42);
            Check(g.Map.Width>=576 && g.Map.Height>=512,"legacy minimum viewport");
            Check(g.Sidebar.Right==g.Width && g.Sidebar.Left==g.Map.Right,"map/sidebar shared edge");
            Check(g.Top.Top==0 && g.Top.Bottom==g.Map.Top,"top/map shared edge");
            Check(g.Bottom.Top==g.Map.Bottom && g.Bottom.Bottom==g.Height,"bottom/map shared edge");
            Check(g.Sidebar.Bottom<=g.Height,"complete column fits");
            Check(g.SidebarDataFoot.Bottom<=g.Sidebar.Bottom && g.SidebarBottomBorder.Bottom==g.Height,"information stays with content; bottom border stays at window edge");
            Check(g.SidebarDataFoot.Top==g.SidebarMain.Bottom,"information rows join main column");
            Check(g.SidebarDataFoot.Left==g.SidebarLeftFoot.Right && g.SidebarDataFoot.Right==g.SidebarRightFoot.Left,"footer/content columns share edges");
            Check(g.Top.Left>=0 && g.Top.Right<=g.Map.Right,"top group fits");
            Check(g.Bottom.Left==g.Top.Left && g.Bottom.Right==g.Top.Right,"bars share center");
            if(g.Width>800) Check(Math.Abs((g.Top.Left+g.Top.Right)-(g.Map.Left+g.Map.Right))<=1,"bars centered to half a pixel");
            RoundTrip(g,new PixelRect(0,0,586,58),true);
            RoundTrip(g,new PixelRect(0,570,586,600),true);
            RoundTrip(g,new PixelRect(586,0,800,540),true);
            RoundTrip(g,new PixelRect(586,540,800,600),true);
            RoundTrip(g,new PixelRect(0,0,800,600),false);
            GeometryPoint unused;
            Check(!g.TryUiToLogical(g.Map.Right-1,g.Map.Top+1,true,out unused),"map interior is not UI");
            if(g.Top.Left>0) Check(!g.TryUiToLogical(g.Top.Left-1,1,true,out unused),"filler cannot activate a control");
            Check(g.Generation==42,"immutable generation");
            HdLayout other=new HdLayout(g.Width,g.Height,1,dpi,.2,43);
            // Changing UI settings may move the map origin but a native map
            // interval remains exactly 64 physical pixels in both snapshots.
            Check((g.Map.Left+64)-g.Map.Left==64 && (other.Map.Left+64)-other.Map.Left==64,"map pitch independent of UI");
        }
        HdLayout legacy=new HdLayout(800,600,1,1,.2,0);
        Check(legacy.Map.Left==10 && legacy.Map.Top==58 && legacy.Map.Right==586 && legacy.Map.Bottom==570,"exact legacy geometry");
        foreach(int w in new int[]{1920,2560,3840})
        {
            HdLayout g=new HdLayout(w,w*9/16,1,1,.2,0);
            Near(g.SidebarWidth,w*.2,"20 percent sidebar default");
        }
        try { new HdLayout(1920,1080,Double.NaN,1,.2,0); throw new Exception("NaN accepted"); } catch(ArgumentException) { checks++; }
        try { new HdLayout(1920,1080,1,1,Double.PositiveInfinity,0); throw new Exception("infinity accepted"); } catch(ArgumentException) { checks++; }
        Console.WriteLine("PASS: {0} HD layout/round-trip checks; five target resolutions, ultrawide, DPI and fractional UI scales.",checks);
    }
}
