using System;
using System.IO;
using System.Runtime.InteropServices;
using Imp2Net.Graphics;

namespace Imp2Net
{
    public static class WindowedMapStartup
    {
        [DllImport("ole32.dll")] static extern int CoInitialize(IntPtr reserved);
        static bool started;
        static NativeMapCache cache;
        public static int Initialize(IntPtr reserved)
        {
            int hr = CoInitialize(reserved);
            if (started) return hr;
            started = true;
            // Opt-in file is deployed by BuildWindowedMap.ps1. Normal multiplayer
            // installation uses the original display behavior.
            if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "windowed-map.ini"))) return hr;
            try
            {
                NativeMapCache.ValidateProfile(); NativeMapInput.Validate();
                cache = NativeMapCache.Install();
                NativeMapRenderer.Initialize();
                NativeMapInput.Install();
                NativeMapInput.OriginalMouseDispatch = WindowedMapBridge.DispatchOriginalMouse;
                WindowedMapBridge.MapMouseMessage = NativeMapInput.HandleMouse;
                WindowedMapBridge.Initialize(reserved);
                NativeMapRenderer.Log("Startup complete.");
            }
            catch (Exception e)
            {
                NativeMapRenderer.Log("Installation refused: " + e);
                NativeMapInput.Clear();
                NativePatch.Rollback();
                if (cache != null) { cache.Dispose(); cache = null; }
                // Do not continue with a partially installed display bridge.
                Environment.Exit(1);
            }
            return hr;
        }
    }
}
