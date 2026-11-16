using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Imp2Net;

class TestNativeMapInput
{
    static int checks;
    static int visible;
    static int frees;
    static int VisibilityFixture(IntPtr map) { return visible; }
    static void FreeFixture(IntPtr map) { frees++; }
    static void SetNativeDelegate(string field, string method)
    {
        FieldInfo info = typeof(NativeMapInput).GetField(field, BindingFlags.NonPublic|BindingFlags.Static);
        info.SetValue(null, Delegate.CreateDelegate(info.FieldType, typeof(TestNativeMapInput).GetMethod(method, BindingFlags.NonPublic|BindingFlags.Static)));
    }
    static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
    static void Main()
    {
        IntPtr map = Marshal.AllocHGlobal(8), point = Marshal.AllocHGlobal(8);
        IntPtr column = Marshal.AllocHGlobal(2), row = Marshal.AllocHGlobal(2), subregion = Marshal.AllocHGlobal(2);
        try
        {
            int decodedX = 0, decodedY = 0;
            NativeMapInput.ScreenToTileDelegate decoder = delegate(IntPtr self, IntPtr p, IntPtr c, IntPtr r, IntPtr s)
            {
                decodedX = Marshal.ReadInt32(p); decodedY = Marshal.ReadInt32(p, 4);
                Marshal.WriteInt16(c, -3); Marshal.WriteInt16(r, 59); Marshal.WriteInt16(s, 3);
            };
            typeof(NativeMapInput).GetField("original", BindingFlags.NonPublic|BindingFlags.Static).SetValue(null, decoder);
            MethodInfo decode = typeof(NativeMapInput).GetMethod("DecodePoint", BindingFlags.NonPublic|BindingFlags.Static);
            Action<IntPtr> callDecode = delegate(IntPtr self) { decode.Invoke(null, new object[] { self,point,column,row,subregion }); };
            Marshal.WriteInt32(point, 2); Marshal.WriteInt32(point, 4, 4);
            NativeMapInput.SetActiveMap(map);
            NativeMapInput.SetViewportSize(1920,1080);
            NativeMapInput.OriginalMouseDispatch = delegate(uint message, IntPtr wParam, int x, int y)
            {
                Check(message == 0x201, "button message preserved");
                Check(wParam == (IntPtr)5, "button/modifier flags preserved");
                Check(x > 400 && x < 550 && y > 400 && y < 550, "extended interior point routes away from original edges");
                Check(NativeMapInput.IsDispatching, "override scope entered");
                callDecode(map);
                Check(decodedX == 1490 && decodedY == 842, "native density point reaches decoder");
                Check(Marshal.ReadInt32(point) == 2 && Marshal.ReadInt32(point,4) == 4, "caller point unchanged");
                Check(NativeMapInput.LastColumn == -3 && NativeMapInput.LastRow == 59 && NativeMapInput.LastSubregion == 3,
                    "signed 16-bit native results preserved");
                callDecode(IntPtr.Add(map,4));
                Check(decodedX == 2 && decodedY == 4, "unrelated map untouched");
                return IntPtr.Zero;
            };
            Check(NativeMapInput.HandleMouse(0x201,(IntPtr)5,1500,900), "map message consumed once");
            Check(!NativeMapInput.IsDispatching, "override scope exited");
            NativeMapInput.Clear(); NativeMapInput.SetActiveMap(map);
            Check(!NativeMapInput.HandleMouse(0x201,IntPtr.Zero,1800,900), "anchored sidebar remains native UI");
            Check(!NativeMapInput.HandleMouse(0x201,IntPtr.Zero,1500,1070), "anchored bottom remains native UI");
            callDecode(map);
            Check(decodedX == 2 && decodedY == 4, "non-event decoder untouched");
            Check(!NativeMapInput.HandleMouse(0x100,IntPtr.Zero,1500,900), "keyboard passes through");
            NativeMapInput.PhysicalCursor = delegate { return new NativeMapInput.Point { X=1600,Y=1000 }; };
            NativeMapInput.OriginalMouseDispatch = delegate(uint message, IntPtr wp, int x, int y)
            {
                callDecode(map);
                Check(decodedX == 1590 && decodedY == 942, "native tracking samples live cursor");
                throw new InvalidOperationException("tracking stopped");
            };
            try { NativeMapInput.HandleMouse(0x200,IntPtr.Zero,1500,900); throw new Exception("missing dispatch exception"); }
            catch(InvalidOperationException) { Check(!NativeMapInput.IsDispatching, "throwing handler restores override scope"); }
            NativeMapInput.Clear(map);
            Check(!NativeMapInput.HandleMouse(0x202,IntPtr.Zero,1500,900), "destroyed map no longer routes");
            Marshal.WriteInt32(map,0x703900);
            SetNativeDelegate("isShown","VisibilityFixture");
            SetNativeDelegate("originalFree","FreeFixture");
            typeof(NativeMapInput).GetField("installed",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,true);
            NativeMapInput.SetActiveMap(map);
            visible=0; Check(!NativeMapInput.IsActiveMapVisible,"hidden map suppresses input/composition");
            visible=1; Check(NativeMapInput.IsActiveMapVisible,"shown map enables input/composition");
            Marshal.WriteInt32(map,0x12345678);
            Check(!NativeMapInput.IsActiveMapVisible,"changed map class rejected before native predicate");
            Marshal.WriteInt32(map,0x703900);
            int destroyed=0;
            NativeMapInput.MapDestroyed=delegate(IntPtr self)
            {
                Check(self==map && !NativeMapInput.IsActiveMapVisible,"active pointer cleared before teardown notification");
                Check(frees==0,"notification precedes native resource release");
                destroyed++;
            };
            typeof(NativeMapInput).GetMethod("FreeMap",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{map});
            Check(destroyed==1 && frees==1,"native free preserved after notification");
            Console.WriteLine("Native map input: " + checks + " checks passed.");
        }
        finally
        {
            Marshal.FreeHGlobal(map); Marshal.FreeHGlobal(point); Marshal.FreeHGlobal(column); Marshal.FreeHGlobal(row); Marshal.FreeHGlobal(subregion);
        }
    }
}
