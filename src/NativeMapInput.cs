using System;
using System.Runtime.InteropServices;

namespace Imp2Net
{
    // The native root still hit-tests the original 800x600 UI. Dispatch map
    // messages through an interior proxy, then give the ORIGINAL tile decoder
    // the actual native-pixel map-local point. The scope is restricted to the
    // game thread and active map; unrelated view decoders are untouched.
    public static class NativeMapInput
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Point { public int X, Y; }

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate void ScreenToTileDelegate(IntPtr self, IntPtr point,
            IntPtr column, IntPtr row, IntPtr subregion);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int IsShownDelegate(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr DeleteDelegate(IntPtr self, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void FreeDelegate(IntPtr self);

        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint previous);

        private static readonly int[] Cells = { 0x703AC4, 0x703D54, 0x703FE4 };
        private static readonly int[] DeleteCells = { 0x703904, 0x703B94, 0x703E24 };
        private static readonly int[] DeleteTargets = { 0x405EDE, 0x40374C, 0x40439F };
        private static readonly int[] FreeCells = { 0x70391C, 0x703BAC, 0x703E3C };
        private static readonly ScreenToTileDelegate Hook = DecodePoint;
        private static readonly FreeDelegate FreeHook = FreeMap;
        private static readonly DeleteDelegate[] DeleteHooks = new DeleteDelegate[3];
        private static readonly DeleteDelegate[] OriginalDelete = new DeleteDelegate[3];
        private static FreeDelegate originalFree;
        private static IsShownDelegate isShown;
        private static ScreenToTileDelegate original;
        private static IntPtr activeMap;
        private static bool installed;
        private static int viewportWidth = 800, viewportHeight = 600;
        private static int pressedButtons;
        [ThreadStatic] private static int dispatchDepth;
        [ThreadStatic] private static Point dispatchPoint;

        // Bind to WindowedMapBridge.DispatchOriginalMouse. That method must
        // call the original window procedure directly, without remapping again.
        public static Func<uint, IntPtr, int, int, IntPtr> OriginalMouseDispatch;
        // Optional live pointer sampling is required by native tracking loops
        // that poll the pointer while the original button-down handler blocks.
        public static Func<Point> PhysicalCursor;
        public static Action<string> Trace;
        public static Action<IntPtr> MapDestroyed;
        public static long RoutedMessages { get; private set; }
        public static long DecoderOverrides { get; private set; }
        public static int LastColumn { get; private set; }
        public static int LastRow { get; private set; }
        public static int LastSubregion { get; private set; }
        public static bool IsDispatching { get { return dispatchDepth != 0; } }
        public static bool IsActiveMapVisible
        {
            get
            {
                IntPtr map = activeMap;
                if (map == IntPtr.Zero || !installed || isShown == null) return false;
                int table = Marshal.ReadInt32(map);
                return (table == 0x703900 || table == 0x703B90 || table == 0x703E20) && isShown(map) != 0;
            }
        }

        public static void SetActiveMap(IntPtr map) { if (activeMap != map) pressedButtons = 0; activeMap = map; }
        public static void SetViewportSize(int width, int height)
        { viewportWidth = Math.Max(800,width); viewportHeight = Math.Max(600,height); }
        public static void Clear() { activeMap = IntPtr.Zero; pressedButtons = 0; }
        public static void Clear(IntPtr map) { if (activeMap == map) Clear(); }

        public static void Validate()
        {
            if (IntPtr.Size != 4) throw new PlatformNotSupportedException("Native map input requires x86.");
            foreach (int address in Cells)
                if (Marshal.ReadInt32((IntPtr)address) != 0x4078C9)
                    throw new InvalidOperationException("Unsupported map decoder vtable at 0x" + address.ToString("X8"));
            for (int i = 0; i < DeleteCells.Length; i++)
            {
                if (Marshal.ReadInt32((IntPtr)DeleteCells[i]) != DeleteTargets[i] ||
                    Marshal.ReadInt32((IntPtr)FreeCells[i]) != 0x4052B8 ||
                    Marshal.ReadInt32((IntPtr)(DeleteCells[i] - 4 + 0xF0)) != 0x4082B0)
                    throw new InvalidOperationException("Unsupported map lifecycle vtable.");
            }
            byte[] signature = { 0x53,0x56,0x57,0x8B,0x7C,0x24,0x10,0x8B };
            for (int i = 0; i < signature.Length; i++)
                if (Marshal.ReadByte((IntPtr)0x55A5A0, i) != signature[i])
                    throw new InvalidOperationException("Unsupported native tile decoder.");
            byte[] visibilitySignature = { 0xA1,0x38,0x7A,0x74,0,0x85,0xC0,0x74,0x27 };
            for (int i = 0; i < visibilitySignature.Length; i++)
                if (Marshal.ReadByte((IntPtr)0x49F770, i) != visibilitySignature[i])
                    throw new InvalidOperationException("Unsupported native view visibility predicate.");
        }

        public static void Install()
        {
            if (installed) return;
            Validate();
            original = (ScreenToTileDelegate)Marshal.GetDelegateForFunctionPointer((IntPtr)0x4078C9, typeof(ScreenToTileDelegate));
            originalFree = (FreeDelegate)Marshal.GetDelegateForFunctionPointer((IntPtr)0x4052B8, typeof(FreeDelegate));
            isShown = (IsShownDelegate)Marshal.GetDelegateForFunctionPointer((IntPtr)0x4082B0, typeof(IsShownDelegate));
            for (int i = 0; i < DeleteCells.Length; i++)
            {
                int index = i;
                OriginalDelete[i] = (DeleteDelegate)Marshal.GetDelegateForFunctionPointer((IntPtr)DeleteTargets[i], typeof(DeleteDelegate));
                DeleteHooks[i] = delegate(IntPtr self, uint flags) { NotifyDestroyed(self); return OriginalDelete[index](self, flags); };
            }
            IntPtr replacement = Marshal.GetFunctionPointerForDelegate(Hook);
            int changed = 0;
            try
            {
                foreach (int address in Cells)
                {
                    WritePointer(address, replacement);
                    changed++;
                }
                for (int i = 0; i < DeleteCells.Length; i++) { WritePointer(DeleteCells[i], Marshal.GetFunctionPointerForDelegate(DeleteHooks[i])); changed++; }
                for (int i = 0; i < FreeCells.Length; i++) { WritePointer(FreeCells[i], Marshal.GetFunctionPointerForDelegate(FreeHook)); changed++; }
                installed = true;
            }
            catch
            {
                for (int i = 0; i < changed; i++)
                {
                    if (i < 3) WritePointer(Cells[i], (IntPtr)0x4078C9);
                    else if (i < 6) WritePointer(DeleteCells[i-3], (IntPtr)DeleteTargets[i-3]);
                    else WritePointer(FreeCells[i-6], (IntPtr)0x4052B8);
                }
                throw;
            }
        }

        private static void NotifyDestroyed(IntPtr map)
        {
            if (activeMap != map) return;
            Clear();
            if (MapDestroyed != null) MapDestroyed(map);
        }

        private static void FreeMap(IntPtr self)
        { NotifyDestroyed(self); originalFree(self); }

        private static void WritePointer(int address, IntPtr value)
        {
            uint old;
            if (!VirtualProtect((IntPtr)address, (UIntPtr)4, 0x04, out old))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Marshal.WriteIntPtr((IntPtr)address, value);
            uint ignored;
            if (!VirtualProtect((IntPtr)address, (UIntPtr)4, old, out ignored))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        public static bool HandleMouse(uint message, IntPtr wParam, int physicalX, int physicalY)
        {
            if (activeMap == IntPtr.Zero || OriginalMouseDispatch == null || message < 0x200 || message > 0x209)
                return false;
            bool inside = physicalX >= 10 && physicalX < viewportWidth - 214 && physicalY >= 58 && physicalY < viewportHeight - 30;
            if (!inside && pressedButtons == 0) return false;
            int button = message >= 0x201 && message <= 0x203 ? 1 : message >= 0x204 && message <= 0x206 ? 2 : message >= 0x207 ? 4 : 0;
            bool down = message == 0x201 || message == 0x203 || message == 0x204 || message == 0x206 || message == 0x207 || message == 0x209;
            bool up = message == 0x202 || message == 0x205 || message == 0x208;
            if (down) pressedButtons |= button;
            Point previous = dispatchPoint;
            dispatchPoint = new Point { X = physicalX - 10, Y = physicalY - 58 };
            dispatchDepth++;
            RoutedMessages++;
            try
            {
                // Proportional proxy coordinates retain relative edge behavior;
                // clamping an extended interior point to the old right edge
                // would spuriously trigger edge scrolling. Only dispatch/hit
                // testing uses this proxy. Tile decoding remains 1:1.
                int proxyX, proxyY;
                GetProxyPoint(physicalX, physicalY, out proxyX, out proxyY);
                OriginalMouseDispatch(message, wParam, proxyX, proxyY);
                return true;
            }
            finally { dispatchDepth--; dispatchPoint = previous; if (up) pressedButtons &= ~button; }
        }

        public static bool TryGetProxyPoint(int physicalX, int physicalY, out int proxyX, out int proxyY)
        {
            proxyX = physicalX; proxyY = physicalY;
            if (activeMap == IntPtr.Zero || (dispatchDepth == 0 && pressedButtons == 0)) return false;
            GetProxyPoint(physicalX, physicalY, out proxyX, out proxyY);
            return true;
        }

        private static void GetProxyPoint(int physicalX, int physicalY, out int proxyX, out int proxyY)
        {
            int width = viewportWidth - 224, height = viewportHeight - 88;
            int localX = Math.Max(0, Math.Min(width-1,physicalX-10));
            int localY = Math.Max(0, Math.Min(height-1,physicalY-58));
            proxyX = 10 + (int)((long)localX * 575 / (width-1));
            proxyY = 58 + (int)((long)localY * 511 / (height-1));
        }

        private static void DecodePoint(IntPtr self, IntPtr point, IntPtr column, IntPtr row, IntPtr subregion)
        {
            if (dispatchDepth == 0 || self != activeMap || point == IntPtr.Zero)
            {
                original(self, point, column, row, subregion);
                return;
            }
            Point mapped = dispatchPoint;
            // Nested native TrackMouse loops sample current pointer coordinates;
            // use real screen/client APIs, not the game's remapped IAT queries.
            if (PhysicalCursor != null)
            {
                Point physical = PhysicalCursor();
                mapped.X = physical.X - 10; mapped.Y = physical.Y - 58;
            }
            IntPtr buffer = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.StructureToPtr(mapped, buffer, false);
                original(self, buffer, column, row, subregion);
                DecoderOverrides++;
                LastColumn = Marshal.ReadInt16(column);
                LastRow = Marshal.ReadInt16(row);
                LastSubregion = Marshal.ReadInt16(subregion);
                if (Trace != null) Trace("input map-local=(" + mapped.X + "," + mapped.Y + ") tile=(" + LastColumn + "," + LastRow + "," + LastSubregion + ")");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
