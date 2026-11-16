using System;
using System.IO;
using System.Runtime.InteropServices;
using Imp2Net;
class TestNativeMapEdges
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Classify(int x,int y,int width,int height);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAlloc(IntPtr address,UIntPtr length,uint type,uint protection);
    [DllImport("kernel32.dll")] static extern bool VirtualProtect(IntPtr address,UIntPtr length,uint protection,out uint previous);
    [DllImport("kernel32.dll")] static extern bool VirtualFree(IntPtr address,UIntPtr length,uint type);
    static int Offset(byte[] file,int address)
    {
        int pe=BitConverter.ToInt32(file,0x3c),count=BitConverter.ToUInt16(file,pe+6),section=pe+24+BitConverter.ToUInt16(file,pe+20);
        for(int i=0;i<count;i++,section+=40) {
            int rva=BitConverter.ToInt32(file,section+12),length=BitConverter.ToInt32(file,section+16);
            if(address-0x400000>=rva && address-0x400000<rva+length)
                return BitConverter.ToInt32(file,section+20)+address-0x400000-rva;
        }
        throw new Exception("Native edge instructions not found");
    }
    static void Main(string[] args)
    {
        byte[] file=File.ReadAllBytes(args[0]);int offset=Offset(file,0x4b6d30);
        byte[] expected={0x32,0xdb,0x83,0xf8,4,0x7f,4,0xb3,8,0xeb,9,0x83,0xc2,0xfc,0x3b,0xc2,0x7c,2,0xb3,4,
            0x83,0xfe,4,0x7f,5,0x80,0xcb,1,0xeb,10,0x83,0xc1,0xfc,0x3b,0xf1,0x7c,3,0x80,0xcb,2};
        for(int i=0;i<expected.Length;i++)if(file[offset+i]!=expected[i])throw new Exception("Unsupported native edge classifier");
        // Supply the original classifier's register inputs; preserve callee-save
        // registers and return its unmodified native direction-bit result.
        byte[] prefix={0x53,0x56,0x8b,0x44,0x24,12,0x8b,0x74,0x24,16,0x8b,0x54,0x24,20,0x8b,0x4c,0x24,24};
        byte[] suffix={0x0f,0xb6,0xc3,0x5e,0x5b,0xc3};
        IntPtr code=VirtualAlloc(IntPtr.Zero,(UIntPtr)4096,0x3000,4);
        if(code==IntPtr.Zero)throw new Exception("Cannot allocate edge fixture");
        try {
            Marshal.Copy(prefix,0,code,prefix.Length);
            Marshal.Copy(file,offset,IntPtr.Add(code,prefix.Length),expected.Length);
            Marshal.Copy(suffix,0,IntPtr.Add(code,prefix.Length+expected.Length),suffix.Length);
            uint previous;if(!VirtualProtect(code,(UIntPtr)4096,0x20,out previous))throw new Exception("Cannot protect edge fixture");
            Classify classify=(Classify)Marshal.GetDelegateForFunctionPointer(code,typeof(Classify));
            int checks=0;
            if(NativeMapInput.EdgeScrollMargin!=128)throw new Exception("Configured default edge margin changed");checks++;
            NativeMapInput.SetActiveMap(new IntPtr(1));
            int[,] layouts={{10,58,576,512},{18,104,1518,922},{36,208,3036,1844},{24,139,2902,1229}};
            foreach(int margin in new int[]{16,32,64,128}) {
            NativeMapInput.SetEdgeScrollMargin(margin);
            for(int layout=0;layout<layouts.GetLength(0);layout++) {
                int left=layouts[layout,0],top=layouts[layout,1],width=layouts[layout,2],height=layouts[layout,3];
                NativeMapInput.SetMapBounds(left,top,width,height);
                int[,] points={{left,top+height/2,8},{left+margin-1,top+height/2,8},{left+margin,top+height/2,0},
                    {left+width-1,top+height/2,4},{left+width/2,top,1},{left+width/2,top+height-1,2},
                    {left,top,9},{left+width-1,top+height-1,6},{left+width/2,top+height/2,0},
                    {left-1,top+height/2,-1},{left+width,top+height/2,-1},{left+width/2,top-1,-1},{left+width/2,top+height,-1}};
                foreach(int nativeWidth in new int[]{576,800,1280})
                foreach(int nativeHeight in new int[]{512,600,900})
                for(int p=0;p<points.GetLength(0);p++) {
                    NativeMapInput.Point edge=NativeMapInput.EdgeScrollPoint(points[p,0],points[p,1],nativeWidth,nativeHeight);
                    int result=edge.X<=-200 || edge.Y<=-200 ? -1 : classify(edge.X,edge.Y,nativeWidth,nativeHeight);
                    if(result!=points[p,2])throw new Exception("Native edge direction mismatch at layout "+layout+" point "+p+": "+result);
                    checks++;
                }
            }
            }
            NativeMapInput.SetEdgeScrollMargin(-1);if(NativeMapInput.EdgeScrollMargin!=8)throw new Exception("Edge margin minimum missing");checks++;
            NativeMapInput.SetEdgeScrollMargin(999);if(NativeMapInput.EdgeScrollMargin!=128)throw new Exception("Edge margin maximum missing");checks++;
            NativeMapInput.Clear();
            if(NativeMapInput.EdgeScrollPoint(40,200,800,600).X!=-1000)throw new Exception("Inactive map can scroll");checks++;
            Console.WriteLine("PASS: {0} HD edge-scroll checks using the original native direction instructions.",checks);
        }
        finally { VirtualFree(code,UIntPtr.Zero,0x8000); }
    }
}
