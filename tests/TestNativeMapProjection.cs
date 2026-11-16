using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Imp2Net;
class TestNativeMapProjection
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void EdgeCall(IntPtr view,IntPtr point);
    static int edgeVisible;
    static int EdgeVisibility(IntPtr map) { return edgeVisible; }
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAlloc(IntPtr p,UIntPtr n,uint allocation,uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFree(IntPtr p,UIntPtr n,uint kind);
    static int Main(string[] args)
    {
        byte[] file=File.ReadAllBytes(args[0]);
        int pe=BitConverter.ToInt32(file,0x3c),sections=BitConverter.ToUInt16(file,pe+6),s=pe+24+BitConverter.ToUInt16(file,pe+20),offset=-1;
        for(int i=0;i<sections;i++,s+=40)
        {
            int rva=BitConverter.ToInt32(file,s+12),length=BitConverter.ToInt32(file,s+16);
            if(0x14f680>=rva && 0x14f680<rva+length) offset=BitConverter.ToInt32(file,s+20)+0x14f680-rva;
        }
        if(offset<0 || file[offset]!=0x0f || file[offset+0x8c]!=0xc3) throw new Exception("Unsupported native projection.");
        IntPtr code=VirtualAlloc(IntPtr.Zero,(UIntPtr)4096,0x3000,0x40),map=Marshal.AllocHGlobal(256),xy=Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(file,offset,code,0x8d);
            FieldInfo field=typeof(NativeMapRenderer).GetField("project",BindingFlags.NonPublic|BindingFlags.Static);
            field.SetValue(null,Marshal.GetDelegateForFunctionPointer(code,field.FieldType));
            int checks=0;
            Marshal.WriteInt32(map,0x34,576);Marshal.WriteInt32(map,0x38,512);
            typeof(NativeMapRenderer).GetField("width",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,1518);
            typeof(NativeMapRenderer).GetField("height",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,922);
            using(NativeMapRenderer.UseNativeMapBounds(map)) {
                if(Marshal.ReadInt32(map,0x34)!=1518 || Marshal.ReadInt32(map,0x38)!=922)throw new Exception("Map draw extent missing");checks++;
                using(NativeMapRenderer.UseNativeMapBounds(map)) { }
                if(Marshal.ReadInt32(map,0x34)!=1518)throw new Exception("Nested map bounds lost");checks++;
            }
            if(Marshal.ReadInt32(map,0x34)!=576 || Marshal.ReadInt32(map,0x38)!=512)throw new Exception("Map steals logical sidebar hit area after drawing");checks++;
            try { using(NativeMapRenderer.UseNativeMapBounds(map)) throw new InvalidOperationException("fixture"); } catch(InvalidOperationException) { }
            if(Marshal.ReadInt32(map,0x34)!=576 || Marshal.ReadInt32(map,0x38)!=512)throw new Exception("Exceptional draw leaves enlarged map hit area");checks++;
            foreach(int cameraX in new int[]{0,4160,6848})
            foreach(int cameraY in new int[]{0,1344,3328})
            foreach(int row in new int[]{0,1,25,59})
            {
                int column=69;Marshal.WriteInt32(map,0x64,cameraX);Marshal.WriteInt32(map,0x68,cameraY);
                NativeMapRenderer.ProjectForTerrain(map,row*108+column,xy);
                int x=column*64+(row%2)*32-cameraX;while(x< -64)x+=6912;
                if(Marshal.ReadInt16(xy)!=x || Marshal.ReadInt16(xy,2)!=row*64-cameraY) throw new Exception("Native projection output ordering/ABI mismatch.");
                checks++;
            }
            IntPtr view=Marshal.AllocHGlobal(96),vtable=Marshal.AllocHGlobal(0x150),point=Marshal.AllocHGlobal(8);
            try {
                // Execute the real managed edge callback through the native
                // ThisCall call-site shape, including callee stack cleanup.
                byte[] convert={0x8b,0x44,0x24,4,0xff,0x00,0xff,0x40,4,0xc2,4,0};
                Marshal.Copy(convert,0,IntPtr.Add(code,256),convert.Length);
                Marshal.WriteIntPtr(view,vtable);Marshal.WriteIntPtr(vtable,0x148,IntPtr.Add(code,256));
                Marshal.WriteInt32(view,0x34,800);Marshal.WriteInt32(view,0x38,600);
                Delegate hook=(Delegate)typeof(NativeMapRenderer).GetField("edgePointHook",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
                byte[] wrapper={0x8b,0x4c,0x24,4,0xff,0x74,0x24,8,0,0,0,0,0,0x90,0xc3};
                Array.Copy(NativePatch.Branch(0xe8,IntPtr.Add(code,520).ToInt32(),Marshal.GetFunctionPointerForDelegate(hook).ToInt32()),0,wrapper,8,5);
                Marshal.Copy(wrapper,0,IntPtr.Add(code,512),wrapper.Length);
                EdgeCall call=(EdgeCall)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(code,512),typeof(EdgeCall));
                FieldInfo shown=typeof(NativeMapInput).GetField("isShown",BindingFlags.NonPublic|BindingFlags.Static);
                shown.SetValue(null,Delegate.CreateDelegate(shown.FieldType,typeof(TestNativeMapProjection).GetMethod("EdgeVisibility",BindingFlags.NonPublic|BindingFlags.Static)));
                typeof(NativeMapInput).GetField("installed",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,true);
                typeof(NativeMapRenderer).GetField("active",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,map);
                Marshal.WriteInt32(map,0x703900);NativeMapInput.SetActiveMap(map);NativeMapInput.SetMapBounds(18,104,1518,922);
                int[,] cases={{1535,104,1,799,0},{777,565,1,400,300},{1536,565,1,-1000,-1000},{1535,104,0,78,89}};
                for(int i=0;i<cases.GetLength(0);i++) {
                    int px=cases[i,0],py=cases[i,1];edgeVisible=cases[i,2];
                    NativeMapInput.PhysicalCursor=delegate { return new NativeMapInput.Point {X=px,Y=py}; };
                    Marshal.WriteInt32(point,77);Marshal.WriteInt32(point,4,88);call(view,point);
                    if(Marshal.ReadInt32(point)!=cases[i,3] || Marshal.ReadInt32(point,4)!=cases[i,4])throw new Exception("Native edge callback ABI/visibility mismatch");checks++;
                }
                NativeMapInput.Clear();edgeVisible=1;Marshal.WriteInt32(point,77);Marshal.WriteInt32(point,4,88);call(view,point);
                if(Marshal.ReadInt32(point)!=78 || Marshal.ReadInt32(point,4)!=89)throw new Exception("Inactive edge callback changed native conversion");checks++;
            }
            finally { Marshal.FreeHGlobal(view);Marshal.FreeHGlobal(vtable);Marshal.FreeHGlobal(point); }
            Console.WriteLine("PASS: {0} native projection ABI/output-order checks, executing original game instructions.",checks);return 0;
        }
        finally { Marshal.FreeHGlobal(xy);Marshal.FreeHGlobal(map);if(code!=IntPtr.Zero)VirtualFree(code,UIntPtr.Zero,0x8000); }
    }
}
