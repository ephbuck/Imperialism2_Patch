using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Imp2Net;
using Imp2Net.Graphics;
class VerifyWindowBridge
{
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc,int x,int y);
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process,uint flags);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateEnhMetaFileW(IntPtr reference,string filename,IntPtr bounds,string description);
    [DllImport("gdi32.dll")] static extern IntPtr CloseEnhMetaFile(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteEnhMetaFile(IntPtr metafile);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MetaRecord(IntPtr dc,IntPtr handles,IntPtr record,int count,IntPtr data);
    [DllImport("gdi32.dll")] static extern bool EnumEnhMetaFile(IntPtr dc,IntPtr metafile,MetaRecord callback,IntPtr data,IntPtr bounds);
    static int checks;
    static void Eq(int actual,int expected,string label) { if(actual!=expected) throw new Exception(label+": "+actual+" != "+expected); checks++; }
    static void Set(string name,object value) { typeof(WindowedMapBridge).GetProperty(name).GetSetMethod(true).Invoke(null,new object[]{value}); }
    static IntPtr Field(string name) { return (IntPtr)typeof(WindowedMapBridge).GetField(name,BindingFlags.NonPublic|BindingFlags.Static).GetValue(null); }
    static void Layout(HdLayout layout)
    {
        Set("ClientWidth",layout.Width);Set("ClientHeight",layout.Height);Set("Layout",layout);
        typeof(WindowedMapBridge).GetMethod("ReplaceMapBacking",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{layout.Width,layout.Height});
    }
    static void CheckSinglePublish(bool active)
    {
        // An enhanced-metafile DC records the commands that reach the visible
        // destination. Hundreds of background/control draws must stay offscreen.
        IntPtr recording=CreateEnhMetaFileW(IntPtr.Zero,null,IntPtr.Zero,null);
        if(recording==IntPtr.Zero)throw new Exception("Cannot record presentation");
        IntPtr metafile;
        try { WindowedMapBridge.PresentFrame(recording,active); }
        finally { metafile=CloseEnhMetaFile(recording); }
        if(metafile==IntPtr.Zero)throw new Exception("Cannot close presentation recording");
        try {
            int commands=0,blits=0;
            MetaRecord inspect=delegate(IntPtr dc,IntPtr handles,IntPtr record,int count,IntPtr data) {
                int type=Marshal.ReadInt32(record);
                if(type!=1 && type!=14) { commands++;if(type==76)blits++; }
                return 1;
            };
            if(!EnumEnhMetaFile(IntPtr.Zero,metafile,inspect,IntPtr.Zero,IntPtr.Zero))throw new Exception("Cannot enumerate presentation recording");
            Eq(commands,1,"completed frame is the only visible drawing command");
            Eq(blits,1,"completed frame publishes with one BitBlt");
        }
        finally { DeleteEnhMetaFile(metafile); }
    }
    static void Main()
    {
        IntPtr bmi=Marshal.AllocHGlobal(64),bits=Marshal.AllocHGlobal(16);
        try
        {
            Layout(new HdLayout(1280,900,1,1,.2,1));
            for(int i=0;i<64;i++) Marshal.WriteByte(bmi,i,0);
            Marshal.WriteInt32(bmi,0,40);Marshal.WriteInt32(bmi,4,2);Marshal.WriteInt32(bmi,8,2);
            Marshal.WriteInt16(bmi,12,1);Marshal.WriteInt16(bmi,14,32);
            Marshal.WriteInt32(bits,0,0xff);Marshal.WriteInt32(bits,4,0xff00);Marshal.WriteInt32(bits,8,0xff0000);Marshal.WriteInt32(bits,12,0xffffff);
            WindowedMapBridge.PresentNativeMapDib(bits,bmi,0,0,2,2,10,58);
            IntPtr dc=Field("mapDc");
            Eq((int)GetPixel(dc,10,58),0xff,"bottom-up red");Eq((int)GetPixel(dc,11,58),0xffffff,"bottom-up white");
            Eq((int)GetPixel(dc,10,59),0xff0000,"bottom-up blue");
            WindowedMapBridge.PresentNativeMapDib(bits,bmi,0,0,2,1,10,60);Eq((int)GetPixel(dc,10,60),0xff,"partial top row");
            Marshal.WriteInt32(bmi,8,-2);WindowedMapBridge.PresentNativeMapDib(bits,bmi,0,0,2,2,10,62);Eq((int)GetPixel(dc,10,62),0xff0000,"top-down blue");
            Marshal.WriteInt32(bmi,8,2);Marshal.WriteInt16(bmi,14,16);Marshal.WriteInt32(bmi,16,3);
            Marshal.WriteInt32(bmi,40,0xf800);Marshal.WriteInt32(bmi,44,0x07e0);Marshal.WriteInt32(bmi,48,0x001f);
            Marshal.WriteInt32(bits,0,0x07e0001f);Marshal.WriteInt32(bits,4,unchecked((int)0xfffff800));
            WindowedMapBridge.PresentNativeMapDib(bits,bmi,0,0,2,2,10,64);
            Eq((int)GetPixel(dc,10,64),0xff,"RGB565 masks red");Eq((int)GetPixel(dc,10,65),0xff0000,"RGB565 masks blue");
            Marshal.WriteInt32(bmi,16,0);Marshal.WriteInt32(bits,0,0x03e0001f);Marshal.WriteInt32(bits,4,0x7fff7c00);
            WindowedMapBridge.PresentNativeMapDib(bits,bmi,0,0,2,2,10,66);
            Eq((int)GetPixel(dc,10,66),0xff,"BI_RGB 16-bit RGB555 red");Eq((int)GetPixel(dc,11,66),0xffffff,"BI_RGB RGB555 white");
            int[,] sizes={{800,600},{1280,900},{1920,1080},{2560,1440},{3840,2160},{3440,1440}};
            uint steadyHandles=0;
            for(int r=0;r<sizes.GetLength(0);r++)
            foreach(double dpi in new double[]{1,1.5,2})
            foreach(double ui in new double[]{1,1.25})
            {
                HdLayout layout=new HdLayout(sizes[r,0],sizes[r,1],ui,dpi,.2,r+2);Layout(layout);
                using(Graphics g=Graphics.FromHdc(Field("uiDc"))) { g.Clear(Color.FromArgb(50,100,140));g.FillRectangle(Brushes.Orange,640,400,100,32);g.FillRectangle(Brushes.Lime,690,575,4,4);g.FillRectangle(Brushes.Yellow,690,594,4,2); }
                using(Graphics g=Graphics.FromHdc(Field("mapDc"))) { g.Clear(Color.FromArgb(30,90,120));g.FillRectangle(Brushes.Red,74,122,64,64); }
                using(Bitmap output=new Bitmap(layout.Width,layout.Height,PixelFormat.Format32bppArgb))
                {
                    using(Graphics g=Graphics.FromImage(output))
                    {
                        g.Clear(Color.Magenta);IntPtr dest=g.GetHdc();
                        try { WindowedMapBridge.PresentFrame(dest,true); }
                        finally { g.ReleaseHdc(dest); }
                    }
                    BitmapData locked=output.LockBits(new Rectangle(0,0,output.Width,output.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
                    try
                    {
                        int[] row=new int[output.Width];
                        for(int y=0;y<output.Height;y++)
                        {
                            Marshal.Copy(IntPtr.Add(locked.Scan0,y*locked.Stride),row,0,row.Length);
                            for(int x=0;x<row.Length;x++) if((row[x]&0xffffff)==0xff00ff)
                                throw new Exception("Unpainted UI/map chunk at "+x+","+y+" in "+layout);
                        }
                        checks++;
                    }
                    finally { output.UnlockBits(locked); }
                    int mx=layout.Map.Left,my=layout.Map.Top;
                    Eq(output.GetPixel(mx+64,my+64).ToArgb()&0xffffff,0xff0000,"map landmark starts at native offset");
                    Eq(output.GetPixel(mx+127,my+127).ToArgb()&0xffffff,0xff0000,"native 64px footprint retained");
                    Eq(output.GetPixel(mx+128,my+128).ToArgb()&0xffffff,0x1e5a78,"landmark never enlarged by UI scale");
                    Eq(output.GetPixel(layout.Sidebar.Left,0).ToArgb()&0xffffff,0x32648c,"sidebar shared edge painted");
                    Eq(output.GetPixel(0,layout.Height-1).ToArgb()&0xffffff,0x32648c,"footer filler painted");
                    GeometryPoint information=layout.UiToPhysical(692,577,true);
                    Eq(output.GetPixel((int)information.X,(int)information.Y).ToArgb()&0xffffff,0x00ff00,"last information row survives sidebar composition");
                    GeometryPoint border=layout.UiToPhysical(692,595,true);
                    Eq(output.GetPixel((int)border.X,(int)border.Y).ToArgb()&0xffffff,0xffff00,"sidebar bottom border stays at window bottom");
                    if(layout.SidebarBottomBorder.Top>layout.SidebarDataFoot.Bottom+2) {
                        int fillerY=(layout.SidebarDataFoot.Bottom+layout.SidebarBottomBorder.Top)/2;
                        Eq(output.GetPixel((int)information.X,fillerY).ToArgb()&0xffffff,0x32648c,"first selected unit cannot repeat labels in filler");
                    }
                }
                var physical=WindowedMapBridge.LogicalToPhysical(new WindowedMapBridge.Point{X=650,Y=580},true);
                var logical=WindowedMapBridge.PhysicalToLogical(physical,true);
                if(Math.Abs(logical.X-650)>1 || Math.Abs(logical.Y-580)>1) throw new Exception("footer hit mismatch");
                checks++;
                var ignored=WindowedMapBridge.PhysicalToLogical(new WindowedMapBridge.Point{X=layout.Map.Right-1,Y=layout.Map.Top+1},true);
                Eq(ignored.X,-1000,"UI mapping cannot alias map interior");
                uint handles=GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle,0);
                if(steadyHandles==0)steadyHandles=handles;
                if(handles>steadyHandles+2)throw new Exception("GDI handles grow across resize: "+steadyHandles+" -> "+handles);
                checks++;
            }
            CheckSinglePublish(true);
            CheckSinglePublish(false);
            Console.WriteLine("PASS: {0} real GDI compositor/DIB/input checks; all pixels covered at 36 resolution/DPI/UI combinations. Gameplay is separate.",checks);
        }
        finally { Marshal.FreeHGlobal(bits);Marshal.FreeHGlobal(bmi); }
    }
}
