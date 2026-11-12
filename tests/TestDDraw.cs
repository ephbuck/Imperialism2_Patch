using System;
using System.IO;
using System.Runtime.InteropServices;

internal static class TestDDraw
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibraryA(string lpLibFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DirectDrawCreateClipperDelegate(uint dwFlags, out IntPtr lplpDDClipper, IntPtr pUnkOuter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr thisPtr);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("ASSERT FAILED: " + message);
        }
    }

    public static int Main(string[] args)
    {
        string ddrawPath = args.Length > 0 ? args[0] : "ddraw.dll";
        Console.WriteLine("Testing DirectDraw DLL: " + Path.GetFullPath(ddrawPath));

        IntPtr hModule = LoadLibraryA(ddrawPath);
        Check(hModule != IntPtr.Zero, "Failed to LoadLibraryA on " + ddrawPath + " (Error: " + Marshal.GetLastWin32Error() + ")");
        Console.WriteLine("[PASS] LoadLibraryA succeeded (hModule = 0x" + hModule.ToString("X") + ")");

        string[] requiredExports = new string[]
        {
            "DirectDrawCreate",
            "DirectDrawCreateClipper",
            "DirectDrawCreateEx",
            "DirectDrawEnumerateExA"
        };

        foreach (string name in requiredExports)
        {
            IntPtr pFunc = GetProcAddress(hModule, name);
            Check(pFunc != IntPtr.Zero, "Missing required export: " + name);
            Console.WriteLine("[PASS] Verified export: " + name + " @ 0x" + pFunc.ToString("X"));
        }

        IntPtr pCreateClipper = GetProcAddress(hModule, "DirectDrawCreateClipper");
        var createClipper = (DirectDrawCreateClipperDelegate)Marshal.GetDelegateForFunctionPointer(pCreateClipper, typeof(DirectDrawCreateClipperDelegate));

        IntPtr pClipper = IntPtr.Zero;
        int hr = createClipper(0, out pClipper, IntPtr.Zero);
        Check(hr == 0, "DirectDrawCreateClipper returned hr = 0x" + hr.ToString("X8"));
        Check(pClipper != IntPtr.Zero, "DirectDrawCreateClipper returned null pointer");
        Console.WriteLine("[PASS] DirectDrawCreateClipper executed successfully (pClipper = 0x" + pClipper.ToString("X") + ")");

        // Release clipper COM interface (IUnknown::Release is vtable slot 2)
        IntPtr pVtbl = Marshal.ReadIntPtr(pClipper);
        IntPtr pRelease = Marshal.ReadIntPtr(pVtbl, 2 * IntPtr.Size);
        var releaseDelegate = (ReleaseDelegate)Marshal.GetDelegateForFunctionPointer(pRelease, typeof(ReleaseDelegate));
        releaseDelegate(pClipper);
        Console.WriteLine("[PASS] Clipper interface released successfully.");

        bool freed = FreeLibrary(hModule);
        Check(freed, "FreeLibrary failed");
        Console.WriteLine("[PASS] DirectDraw DLL unloaded cleanly.");

        Console.WriteLine("\nALL DIRECTDRAW WRAPPER TESTS PASSED SUCCESSFULLY!");
        return 0;
    }
}
