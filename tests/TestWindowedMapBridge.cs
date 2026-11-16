using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Imp2Net;
class VerifyWindowBridge {
 [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc,int x,int y);
 static int checks;
 static void Eq(int actual,int expected,string label) { if(actual != expected) throw new Exception(label+": "+actual+" != "+expected); ++checks; }
 static void Main() {
  typeof(WindowedMapBridge).GetProperty("ClientWidth").GetSetMethod(true).Invoke(null,new object[]{1280});
  typeof(WindowedMapBridge).GetProperty("ClientHeight").GetSetMethod(true).Invoke(null,new object[]{900});
  var p = WindowedMapBridge.PhysicalToLogical(new WindowedMapBridge.Point{X=1076,Y=200},true); Eq(p.X,596,"right UI x"); Eq(p.Y,200,"right UI y");
  p = WindowedMapBridge.PhysicalToLogical(new WindowedMapBridge.Point{X=600,Y=700},true); Eq(p.X,600,"extended map x"); Eq(p.Y,700,"extended map y");
  p = WindowedMapBridge.PhysicalToLogical(new WindowedMapBridge.Point{X=1076,Y=880},true); Eq(p.X,596,"footer right x"); Eq(p.Y,580,"footer y");
  p = WindowedMapBridge.LogicalToPhysical(new WindowedMapBridge.Point{X=596,Y=580},true); Eq(p.X,1076,"footer roundtrip x"); Eq(p.Y,880,"footer roundtrip y");
  p = WindowedMapBridge.PhysicalToLogical(new WindowedMapBridge.Point{X=240,Y=150},false); Eq(p.X,0,"menu x"); Eq(p.Y,0,"menu y");
  IntPtr bmi=Marshal.AllocHGlobal(40), bits=Marshal.AllocHGlobal(16);
  try {
   for(int i=0;i<40;i++) Marshal.WriteByte(bmi,i,0);
   Marshal.WriteInt32(bmi,0,40); Marshal.WriteInt32(bmi,4,2); Marshal.WriteInt32(bmi,8,2); Marshal.WriteInt16(bmi,12,1); Marshal.WriteInt16(bmi,14,32);
   // bottom-up: bottom row blue/green, top row red/white
   Marshal.WriteInt32(bits,0,0x000000ff); Marshal.WriteInt32(bits,4,0x0000ff00); Marshal.WriteInt32(bits,8,0x00ff0000); Marshal.WriteInt32(bits,12,0x00ffffff);
   WindowedMapBridge.PresentNativeMapDib(bits,bmi,0,0,2,2,10,58);
   IntPtr dc=(IntPtr)typeof(WindowedMapBridge).GetField("mapDc",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
   Eq((int)GetPixel(dc,10,58),0x000000ff,"bottom-up top-left red"); Eq((int)GetPixel(dc,11,58),0x00ffffff,"bottom-up top-right white"); Eq((int)GetPixel(dc,10,59),0x00ff0000,"bottom-up bottom-left blue");
   WindowedMapBridge.PresentNativeMapDib(bits,bmi,0,0,2,1,10,60); Eq((int)GetPixel(dc,10,60),0x000000ff,"partial top row");
   Marshal.WriteInt32(bmi,8,-2);
   WindowedMapBridge.PresentNativeMapDib(bits,bmi,0,0,2,2,10,62); Eq((int)GetPixel(dc,10,62),0x00ff0000,"top-down top-left blue");
   Console.WriteLine(checks+" bridge geometry/GDI checks passed");
  } finally { Marshal.FreeHGlobal(bits); Marshal.FreeHGlobal(bmi); }
 }
}
