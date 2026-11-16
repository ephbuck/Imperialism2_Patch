using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
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
                string path=Process.GetCurrentProcess().MainModule.FileName;
                string digest;
                using(SHA256 hash=SHA256.Create()) using(var stream=File.OpenRead(path))
                    digest=BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");
                if(digest!="BE5DA3D719702B2EF1450F17D038D4DB378FA40CD580E44D90E611C8B88E4FAE")
                    throw new InvalidOperationException("Unsupported HD executable SHA-256: "+digest);
                NativeMapRenderer.Log("EXE "+path+" SHA256="+digest);
                NativeMapRenderer.Log("HD UI assembly="+typeof(WindowedMapStartup).Assembly.ManifestModule.ModuleVersionId);
                foreach(ProcessModule module in Process.GetCurrentProcess().Modules)
                    if(module.ModuleName.Equals("ddraw.dll",StringComparison.OrdinalIgnoreCase) || module.ModuleName.Equals("dplayx.dll",StringComparison.OrdinalIgnoreCase))
                        NativeMapRenderer.Log("Module "+module.ModuleName+" "+module.FileName);
                NativeMapCache.ValidateProfile(); NativeMapInput.Validate();
                cache = NativeMapCache.Install();
                NativeMapRenderer.Initialize();
                NativeMapInput.Install();
                NativeMapInput.OriginalMouseDispatch = WindowedMapBridge.DispatchOriginalMouse;
                NativeMapInput.PhysicalCursor = delegate { WindowedMapBridge.Point p=WindowedMapBridge.GetPhysicalCursor(); return new NativeMapInput.Point { X=p.X,Y=p.Y }; };
                WindowedMapBridge.MapCursorToProxy = delegate(int x,int y) {
                    int px,py;
                    if(!NativeMapInput.TryGetProxyPoint(x,y,out px,out py)) return null;
                    return new WindowedMapBridge.Point { X=px,Y=py };
                };
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
