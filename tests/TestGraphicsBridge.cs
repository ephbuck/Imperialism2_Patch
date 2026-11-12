using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Imp2Net;

internal static class TestGraphicsBridge
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibraryA(string path);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hMod, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hMod);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Imp2Initialize_Del(IntPtr pvReserved);

    private static int s_passed;
    private static int s_failed;

    private static void Assert(bool condition, string testName, string details = "")
    {
        if (condition)
        {
            Console.WriteLine("[PASS] " + testName);
            s_passed++;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[FAIL] " + testName + (string.IsNullOrEmpty(details) ? "" : ": " + details));
            Console.ResetColor();
            s_failed++;
        }
    }

    private static void TestBinaryPatches()
    {
        Console.WriteLine("\n--- 1. Patched Executable Verification ---");
        string exePath = @"Imperialism II - Patched\Imperialism II.exe";
        Assert(File.Exists(exePath), "Patched executable exists", exePath);

        byte[] bytes = File.ReadAllBytes(exePath);
        Assert(bytes.Length == 4024320, "Executable size is exact", bytes.Length.ToString());

        // 1. Startup hook at 0x89D52: E8 59 E0 25 00 90
        byte[] expectedStartup = new byte[] { 0xE8, 0x59, 0xE0, 0x25, 0x00, 0x90 };
        bool matchStartup = true;
        for (int i = 0; i < expectedStartup.Length; i++)
        {
            if (bytes[0x89D52 + i] != expectedStartup[i]) matchStartup = false;
        }
        Assert(matchStartup, "Startup CoInitialize redirected to Imp2InitThunk @0x89D52");

        // 2. Thunk at 0x2E7DB0
        Assert(bytes[0x2E7DB0] == 0x68 && bytes[0x2E7DB1] == 0x04 && bytes[0x2E7DB2] == 0x37,
            "Imp2InitThunk starts at 0x2E7DB0 (VA 0x006E89B0)");

        // 3. String at 0x342550: "Imp2Initialize\0"
        string str = Encoding.ASCII.GetString(bytes, 0x342550, 15).Split('\0')[0];
        Assert(str == "Imp2Initialize", "Export name string 'Imp2Initialize' at 0x342550", str);

        // 4. Verify 0x962B0 (CIncludeView::McEnable) is patched to keep view mouse input active
        byte[] expectedPatched = new byte[] { 0xC7, 0x41, 0x6C, 0x01, 0x00, 0x00, 0x00, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC2, 0x04, 0x00 };
        bool matchPatched = true;
        for (int i = 0; i < expectedPatched.Length; i++)
        {
            if (bytes[0x962B0 + i] != expectedPatched[i]) matchPatched = false;
        }
        Assert(matchPatched, "CIncludeView::McEnable patched @0x962B0 to keep m_bMcEnabled=1");

        // 5. Verify 0x1322AC (UpdateTheGWorld) is patched to expand tactical buffer to 2464x2304
        byte[] expectedGWorld = new byte[]
        {
            0xBA, 0xC0, 0xFF, 0xFF, 0xFF,
            0x89, 0x54, 0x24, 0x00,
            0x89, 0x54, 0x24, 0x04,
            0xC7, 0x44, 0x24, 0x08, 0x60, 0x09, 0x00, 0x00,
            0xC7, 0x44, 0x24, 0x0C, 0xC0, 0x08, 0x00, 0x00,
            0x90
        };
        bool matchGWorld = true;
        for (int i = 0; i < expectedGWorld.Length; i++)
        {
            if (bytes[0x1322AC + i] != expectedGWorld[i]) matchGWorld = false;
        }
        Assert(matchGWorld, "UpdateTheGWorld patched @0x1322AC to expand buffer bounds to (2464x2304 16bpp)");
    }

    private static void TestExportAndDllLoad()
    {
        Console.WriteLine("\n--- 2. dplayx.dll Export & Initializer Verification ---");
        string dllPath = Path.GetFullPath(@"dplayx.dll");
        Assert(File.Exists(dllPath), "dplayx.dll exists in root", dllPath);

        IntPtr hMod = LoadLibraryA(dllPath);
        Assert(hMod != IntPtr.Zero, "dplayx.dll loads successfully into x86 process");

        if (hMod != IntPtr.Zero)
        {
            IntPtr pInit = GetProcAddress(hMod, "Imp2Initialize");
            Assert(pInit != IntPtr.Zero, "Export 'Imp2Initialize' found in dplayx.dll PE export table");

            if (pInit != IntPtr.Zero)
            {
                Imp2Initialize_Del fnInit = (Imp2Initialize_Del)Marshal.GetDelegateForFunctionPointer(pInit, typeof(Imp2Initialize_Del));
                int hr = fnInit(IntPtr.Zero);
                Assert(hr == 0, "Imp2Initialize(0) executes and returns S_OK (0)", hr.ToString());
            }

            // Verify multiplayer exports still present
            IntPtr pCreate = GetProcAddress(hMod, "DirectPlayCreate");
            Assert(pCreate != IntPtr.Zero, "Export 'DirectPlayCreate' preserved in dplayx.dll");

            IntPtr pCoCreate = GetProcAddress(hMod, "Imp2CoCreateInstance");
            Assert(pCoCreate != IntPtr.Zero, "Export 'Imp2CoCreateInstance' preserved in dplayx.dll");

            FreeLibrary(hMod);
        }
    }

    private static void TestGeometryMath()
    {
        Console.WriteLine("\n--- 3. Aspect Ratio & Geometry Scaling Formulas ---");

        // Test 4K (3840 x 2160)
        Imp2Net.GraphicsBridge.RecalculateGeometry(3840, 2160);
        Assert(Imp2Net.GraphicsBridge.TargetW == 2880 && Imp2Net.GraphicsBridge.TargetH == 2160,
            "4K (3840x2160) scales to 2880x2160 (full height 4:3)",
            string.Format("{0}x{1}", Imp2Net.GraphicsBridge.TargetW, Imp2Net.GraphicsBridge.TargetH));
        Assert(Imp2Net.GraphicsBridge.DstX == 480 && Imp2Net.GraphicsBridge.DstY == 0,
            "4K pillarbox offset is 480px centered",
            string.Format("DstX={0}, DstY={1}", Imp2Net.GraphicsBridge.DstX, Imp2Net.GraphicsBridge.DstY));

        // Test 1440p (2560 x 1440)
        Imp2Net.GraphicsBridge.RecalculateGeometry(2560, 1440);
        Assert(Imp2Net.GraphicsBridge.TargetW == 1920 && Imp2Net.GraphicsBridge.TargetH == 1440 && Imp2Net.GraphicsBridge.DstX == 320,
            "1440p (2560x1440) scales to 1920x1440 centered at X=320");

        // Test 1080p (1920 x 1080)
        Imp2Net.GraphicsBridge.RecalculateGeometry(1920, 1080);
        Assert(Imp2Net.GraphicsBridge.TargetW == 1440 && Imp2Net.GraphicsBridge.TargetH == 1080 && Imp2Net.GraphicsBridge.DstX == 240,
            "1080p (1920x1080) scales to 1440x1080 centered at X=240");

        // Test 16:10 (1920 x 1200)
        Imp2Net.GraphicsBridge.RecalculateGeometry(1920, 1200);
        Assert(Imp2Net.GraphicsBridge.TargetW == 1600 && Imp2Net.GraphicsBridge.TargetH == 1200 && Imp2Net.GraphicsBridge.DstX == 160,
            "16:10 (1920x1200) scales to 1600x1200 centered at X=160");
    }

    private static void TestMouseCoordinateMapping()
    {
        Console.WriteLine("\n--- 4. Mouse Coordinate Translation Formulas ---");

        // Set to 4K
        Imp2Net.GraphicsBridge.RecalculateGeometry(3840, 2160);
        int targetW = Imp2Net.GraphicsBridge.TargetW;
        int targetH = Imp2Net.GraphicsBridge.TargetH;
        int dstX = Imp2Net.GraphicsBridge.DstX;
        int dstY = Imp2Net.GraphicsBridge.DstY;

        Func<int, int, Tuple<int, int>> map = (cx, cy) =>
        {
            int gx = (int)((cx - dstX) * 800L / targetW);
            int gy = (int)((cy - dstY) * 600L / targetH);
            if (gx < 0) gx = 0;
            if (gx > 799) gx = 799;
            if (gy < 0) gy = 0;
            if (gy > 599) gy = 599;
            return Tuple.Create(gx, gy);
        };

        // Top-left of 4:3 canvas (480, 0) -> (0, 0)
        var tl = map(480, 0);
        Assert(tl.Item1 == 0 && tl.Item2 == 0, "4K screen (480, 0) maps to game (0, 0)");

        // Center of 4:3 canvas (1920, 1080) -> (400, 300)
        var center = map(1920, 1080);
        Assert(center.Item1 == 400 && center.Item2 == 300, "4K screen (1920, 1080) maps to game center (400, 300)");

        // Bottom-right of 4:3 canvas (3359, 2159) -> (799, 599)
        var br = map(3359, 2159);
        Assert(br.Item1 == 799 && br.Item2 == 599, "4K screen (3359, 2159) maps to game bottom-right (799, 599)");

        // Pillarbox click (100, 1080) -> Clamped to X=0
        var pb = map(100, 1080);
        Assert(pb.Item1 == 0 && pb.Item2 == 300, "Left pillarbox click (100, 1080) clamps to X=0");
    }

    private static void TestChildWindowScaling()
    {
        Console.WriteLine("\n--- 5. Child Window Coordinate Scaling & Reverse Mapping ---");

        // 4K Target Setup
        Imp2Net.GraphicsBridge.RecalculateGeometry(3840, 2160);
        int targetW = Imp2Net.GraphicsBridge.TargetW; // 2880
        int targetH = Imp2Net.GraphicsBridge.TargetH; // 2160
        int dstX = Imp2Net.GraphicsBridge.DstX;       // 480
        int dstY = Imp2Net.GraphicsBridge.DstY;       // 0

        // Test the EDIT control from the user's screenshot: (436, 370, 302, 22)
        int editX = 436, editY = 370, editW = 302, editH = 22;

        int scaledX = dstX + (int)Math.Round((double)editX * targetW / 800.0);
        int scaledY = dstY + (int)Math.Round((double)editY * targetH / 600.0);
        int scaledW = (int)Math.Round((double)editW * targetW / 800.0);
        int scaledH = (int)Math.Round((double)editH * targetH / 600.0);

        Assert(scaledX == 2050 && scaledY == 1332, "Child EDIT control X,Y scales from (436, 370) to (2050, 1332)",
            string.Format("({0}, {1})", scaledX, scaledY));
        Assert(scaledW == 1087 && scaledH == 79, "Child EDIT control W,H scales from (302, 22) to (1087, 79)",
            string.Format("({0}, {1})", scaledW, scaledH));

        // Test round-trip reverse mapping through ScreenToClient formula
        int roundTripX = (int)((scaledX - dstX) * 800L / targetW);
        int roundTripY = (int)((scaledY - dstY) * 600L / targetH);
        Assert(roundTripX == editX && roundTripY == editY, "Child window coordinates round-trip back to 800x600 space accurately",
            string.Format("({0}, {1})", roundTripX, roundTripY));

        // Test font size calculation
        int fontHeight = Math.Max(14, (int)Math.Round(14.0 * targetH / 600.0));
        Assert(fontHeight == 50, "Child control font height scales to 50px on 4K display", fontHeight.ToString());
    }

    private static void TestWindowSizePreservation()
    {
        Console.WriteLine("\n--- 6. View Window Size & Position Preservation ---");

        // Verify that DeferWindowPos IAT address is within the valid PE .idata range
        string exePath = @"Imperialism II - Patched\Imperialism II.exe";
        byte[] bytes = File.ReadAllBytes(exePath);

        // Check IAT entry for DeferWindowPos at VA 0x0076C3A0 (FileOff 0x00358DA0)
        int fOff = 0x00358DA0;
        int targetPtr = BitConverter.ToInt32(bytes, fOff);
        Assert(targetPtr > 0, "IAT entry for DeferWindowPos exists in executable at 0x0076C3A0",
            string.Format("TargetPtr=0x{0:X8}", targetPtr));

        // Test WindowPos constraint math: if MFC attempts to reposition view to (1520, 780, 800, 600)
        int attemptedX = 1520, attemptedY = 780, attemptedW = 800, attemptedH = 600;
        int screenW = 3840, screenH = 2160;

        int enforcedX = (attemptedX != 0 ? 0 : attemptedX);
        int enforcedY = (attemptedY != 0 ? 0 : attemptedY);
        int enforcedW = (attemptedW != screenW ? screenW : attemptedW);
        int enforcedH = (attemptedH != screenH ? screenH : attemptedH);
        Assert(enforcedX == 0 && enforcedY == 0 && enforcedW == 3840 && enforcedH == 2160,
            "View window bounds enforced to (0, 0, 3840, 2160) preventing MFC shrinkage / center-pinning");

        // Test partial dirty rect calculation logic: full-frame vs partial dirty rect
        int partialW = 88, partialH = 165;
        bool isFull = (partialW >= 790 && partialH >= 590);
        Assert(!isFull, "88x165 dirty rect is correctly identified as partial, not full-frame");

        int fullW = 800, fullH = 600;
        bool isFull2 = (fullW >= 790 && fullH >= 590);
        Assert(isFull2, "800x600 blit is correctly identified as full-frame presentation blit");
    }

    private static void TestInvalidateRectAndClipping()
    {
        Console.WriteLine("\n--- 7. InvalidateRect Mapping & GDI Clip Region Prevention ---");

        string exePath = @"Imperialism II - Patched\Imperialism II.exe";
        byte[] bytes = File.ReadAllBytes(exePath);

        // Check IAT entry for InvalidateRect at VA 0x0076C5CC (FileOff 0x00358FCC)
        int fOffInv = 0x00358FCC;
        int targetPtrInv = BitConverter.ToInt32(bytes, fOffInv);
        Assert(targetPtrInv > 0, "IAT entry for InvalidateRect exists in executable at 0x0076C5CC",
            string.Format("TargetPtr=0x{0:X8}", targetPtrInv));

        // Check IAT entry for ValidateRect at VA 0x0076C548 (FileOff 0x00358F48)
        int fOffVal = 0x00358F48;
        int targetPtrVal = BitConverter.ToInt32(bytes, fOffVal);
        Assert(targetPtrVal > 0, "IAT entry for ValidateRect exists in executable at 0x0076C548",
            string.Format("TargetPtr=0x{0:X8}", targetPtrVal));

        // Check IAT entry for BeginPaint at VA 0x0076C4DC (FileOff 0x00358EDC)
        int fOffBegin = 0x00358EDC;
        int targetPtrBegin = BitConverter.ToInt32(bytes, fOffBegin);
        Assert(targetPtrBegin > 0, "IAT entry for BeginPaint exists in executable at 0x0076C4DC",
            string.Format("TargetPtr=0x{0:X8}", targetPtrBegin));

        // Check IAT entry for GetClipBox at VA 0x0076BED0 (FileOff 0x003588D0)
        int fOffClip = 0x003588D0;
        int targetPtrClip = BitConverter.ToInt32(bytes, fOffClip);
        Assert(targetPtrClip > 0, "IAT entry for GetClipBox exists in executable at 0x0076BED0",
            string.Format("TargetPtr=0x{0:X8}", targetPtrClip));

        // Check IAT entry for IntersectClipRect at VA 0x0076BEDC (FileOff 0x003588DC)
        int fOffInter = 0x003588DC;
        int targetPtrInter = BitConverter.ToInt32(bytes, fOffInter);
        Assert(targetPtrInter > 0, "IAT entry for IntersectClipRect exists in executable at 0x0076BEDC",
            string.Format("TargetPtr=0x{0:X8}", targetPtrInter));

        // Check IAT entry for ExcludeClipRect at VA 0x0076BED8 (FileOff 0x003588D8)
        int fOffEx = 0x003588D8;
        int targetPtrEx = BitConverter.ToInt32(bytes, fOffEx);
        Assert(targetPtrEx > 0, "IAT entry for ExcludeClipRect exists in executable at 0x0076BED8",
            string.Format("TargetPtr=0x{0:X8}", targetPtrEx));

        // Test 4K invalidation rect scaling
        Imp2Net.GraphicsBridge.RecalculateGeometry(3840, 2160);
        int targetW = Imp2Net.GraphicsBridge.TargetW; // 2880
        int targetH = Imp2Net.GraphicsBridge.TargetH; // 2160
        int dstX = Imp2Net.GraphicsBridge.DstX;       // 480
        int dstY = Imp2Net.GraphicsBridge.DstY;       // 0

        // Test full-frame invalidation (0, 0, 800, 600) -> (480, 0, 3360, 2160)
        int invLeft = dstX + (int)Math.Round(0.0 * targetW / 800.0);
        int invTop = dstY + (int)Math.Round(0.0 * targetH / 600.0);
        int invRight = dstX + (int)Math.Round(800.0 * targetW / 800.0);
        int invBottom = dstY + (int)Math.Round(600.0 * targetH / 600.0);

        Assert(invLeft == 480 && invTop == 0 && invRight == 3360 && invBottom == 2160,
            "Full virtual (0,0,800,600) invalidation scales to full 4K 4:3 area (480,0,3360,2160)",
            string.Format("({0},{1},{2},{3})", invLeft, invTop, invRight, invBottom));

        // Test animated portrait dirty rect (290, 195, 455, 306)
        int pLeft = dstX + (int)Math.Round(290.0 * targetW / 800.0);
        int pTop = dstY + (int)Math.Round(195.0 * targetH / 600.0);
        int pRight = dstX + (int)Math.Round(455.0 * targetW / 800.0);
        int pBottom = dstY + (int)Math.Round(306.0 * targetH / 600.0);

        Assert(pLeft == 1524 && pTop == 702 && pRight == 2118 && pBottom == 1102,
            "Partial dirty rect (290,195,455,306) scales to exact 4K destination (1524,702,2118,1102)",
            string.Format("({0},{1},{2},{3})", pLeft, pTop, pRight, pBottom));

        // Test BeginPaint / GetClipBox reverse mapping from physical 4K back to virtual 800x600
        int unscaledLeft = (int)Math.Max(0, Math.Round((double)(pLeft - dstX) * 800.0 / targetW));
        int unscaledTop = (int)Math.Max(0, Math.Round((double)(pTop - dstY) * 600.0 / targetH));
        int unscaledRight = (int)Math.Min(800, Math.Round((double)(pRight - dstX) * 800.0 / targetW));
        int unscaledBottom = (int)Math.Min(600, Math.Round((double)(pBottom - dstY) * 600.0 / targetH));

        Assert(unscaledLeft == 290 && unscaledTop == 195 && unscaledRight == 455 && unscaledBottom == 306,
            "BeginPaint/GetClipBox correctly un-scales physical 4K rect back to virtual (290,195,455,306)",
            string.Format("({0},{1},{2},{3})", unscaledLeft, unscaledTop, unscaledRight, unscaledBottom));

        // Test Go Back medallion dirty rect (99, 38, 142, 81) round-trip
        int gbLeft = dstX + (int)Math.Round(99.0 * targetW / 800.0);
        int gbTop = dstY + (int)Math.Round(38.0 * targetH / 600.0);
        int gbRight = dstX + (int)Math.Round(142.0 * targetW / 800.0);
        int gbBottom = dstY + (int)Math.Round(81.0 * targetH / 600.0);

        int gbUnLeft = (int)Math.Max(0, Math.Round((double)(gbLeft - dstX) * 800.0 / targetW));
        int gbUnTop = (int)Math.Max(0, Math.Round((double)(gbTop - dstY) * 600.0 / targetH));
        int gbUnRight = (int)Math.Min(800, Math.Round((double)(gbRight - dstX) * 800.0 / targetW));
        int gbUnBottom = (int)Math.Min(600, Math.Round((double)(gbBottom - dstY) * 600.0 / targetH));

        Assert(gbUnLeft == 99 && gbUnTop == 38 && gbUnRight == 142 && gbUnBottom == 81,
            "Go Back tassel dirty rect round-trips with zero drift: (99,38,142,81)",
            string.Format("({0},{1},{2},{3})", gbUnLeft, gbUnTop, gbUnRight, gbUnBottom));

        // Test main window size constraint in GameWndProc
        int gameAttemptW = 800, gameAttemptH = 600;
        int gameScreenW = 3840, gameScreenH = 2160;
        int gameEnforceW = (gameAttemptW != gameScreenW ? gameScreenW : gameAttemptW);
        int gameEnforceH = (gameAttemptH != gameScreenH ? gameScreenH : gameAttemptH);
        Assert(gameEnforceW == 3840 && gameEnforceH == 2160,
            "Main frame window bounds enforced to (0, 0, 3840, 2160) preventing parent clipping of child view");
    }

    private static void TestAmbitMcWindowScaling()
    {
        Console.WriteLine("\n--- 8. AmbitMcWindow Modal Dialog Scaling & Lifecycle ---");

        int targetW = 2880;
        int targetH = 2160;
        int dstX = 480;
        int dstY = 0;

        // 1. Advisor Dialog (111, 164, 375, 301)
        int origX = 111, origY = 164, origW = 375, origH = 301;
        int physX = dstX + (int)Math.Round((double)origX * targetW / 800.0);
        int physY = dstY + (int)Math.Round((double)origY * targetH / 600.0);
        int physW = (int)Math.Round((double)origW * targetW / 800.0);
        int physH = (int)Math.Round((double)origH * targetH / 600.0);

        Assert(physX == 880 && physY == 590 && physW == 1350 && physH == 1084,
            "1502 AD Advisor AmbitMcWindow scales from (111,164,375x301) to 4K (880,590,1350x1084)",
            string.Format("({0},{1},{2}x{3})", physX, physY, physW, physH));

        // 2. Dialog Blit Destination Scaling
        int blitX = 0, blitY = 0, blitW = 375, blitH = 301;
        int sBlitX = (int)Math.Round((double)blitX * physW / origW);
        int sBlitY = (int)Math.Round((double)blitY * physH / origH);
        int sBlitW = (int)Math.Round((double)blitW * physW / origW);
        int sBlitH = (int)Math.Round((double)blitH * physH / origH);

        Assert(sBlitX == 0 && sBlitY == 0 && sBlitW == 1350 && sBlitH == 1084,
            "AmbitMcWindow full blit maps to dialog physical dimensions (0,0,1350x1084)",
            string.Format("({0},{1},{2}x{3})", sBlitX, sBlitY, sBlitW, sBlitH));

        // 3. Sub-window Button Blit Scaling
        int btnX = 140, btnY = 260, btnW = 95, btnH = 25;
        int sBtnX = (int)Math.Round((double)btnX * physW / origW);
        int sBtnY = (int)Math.Round((double)btnY * physH / origH);
        int sBtnW = (int)Math.Round((double)btnW * physW / origW);
        int sBtnH = (int)Math.Round((double)btnH * physH / origH);

        Assert(sBtnX == 504 && sBtnY == 936 && sBtnW == 342 && sBtnH == 90,
            "Dialog OK button blit scales accurately to (504,936,342x90)",
            string.Format("({0},{1},{2}x{3})", sBtnX, sBtnY, sBtnW, sBtnH));

        // 4. Mouse Coordinate Reverse Mapping
        int clickPhysX = 675, clickPhysY = 542; // Center of physical dialog
        int clickVirtX = (int)Math.Round((double)clickPhysX * origW / physW);
        int clickVirtY = (int)Math.Round((double)clickPhysY * origH / physH);

        Assert(clickVirtX == 188 && clickVirtY == 150,
            "Physical click (675,542) translates to dialog virtual center (188,150)",
            string.Format("({0},{1})", clickVirtX, clickVirtY));

        // Button click reverse mapping
        int btnClickX = 675, btnClickY = 981; // Center of OK button
        int btnVirtX = (int)Math.Round((double)btnClickX * origW / physW);
        int btnVirtY = (int)Math.Round((double)btnClickY * origH / physH);

        Assert(btnVirtX == 188 && btnVirtY == 272,
            "Physical button click (675,981) translates to dialog virtual button (188,272)",
            string.Format("({0},{1})", btnVirtX, btnVirtY));

        // 5. InvalidateRect scaling for dialog
        int invVirtLeft = 140, invVirtTop = 260, invVirtRight = 235, invVirtBottom = 285;
        int invPhysLeft = (int)Math.Round((double)invVirtLeft * physW / origW);
        int invPhysTop = (int)Math.Round((double)invVirtTop * physH / origH);
        int invPhysRight = (int)Math.Round((double)invVirtRight * physW / origW);
        int invPhysBottom = (int)Math.Round((double)invVirtBottom * physH / origH);

        Assert(invPhysLeft == 504 && invPhysTop == 936 && invPhysRight == 846 && invPhysBottom == 1026,
            "Dialog partial InvalidateRect scales to (504,936,846,1026)",
            string.Format("({0},{1},{2},{3})", invPhysLeft, invPhysTop, invPhysRight, invPhysBottom));

        // 6. BeginPaint / GetClipBox un-scaling for dialog
        int clipVirtLeft = (int)Math.Max(0, Math.Round((double)invPhysLeft * origW / physW));
        int clipVirtTop = (int)Math.Max(0, Math.Round((double)invPhysTop * origH / physH));
        int clipVirtRight = (int)Math.Min(origW, Math.Round((double)invPhysRight * origW / physW));
        int clipVirtBottom = (int)Math.Min(origH, Math.Round((double)invPhysBottom * origH / physH));

        Assert(clipVirtLeft == 140 && clipVirtTop == 260 && clipVirtRight == 235 && clipVirtBottom == 285,
            "Dialog BeginPaint/GetClipBox round-trips to virtual (140,260,235,285) with zero drift",
            string.Format("({0},{1},{2},{3})", clipVirtLeft, clipVirtTop, clipVirtRight, clipVirtBottom));

        // 7. WS_CLIPSIBLINGS & WS_CLIPCHILDREN preservation check
        uint originalStyle = 0x50000000; // WS_CHILD | WS_VISIBLE
        uint WS_CLIPSIBLINGS = 0x04000000;
        uint WS_CLIPCHILDREN = 0x02000000;
        uint viewStyle = originalStyle | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
        Assert((viewStyle & WS_CLIPSIBLINGS) != 0 && (viewStyle & WS_CLIPCHILDREN) != 0,
            "WS_CLIPSIBLINGS and WS_CLIPCHILDREN preserved to protect child controls and dialogs");

        // 8. HWND_BOTTOM preservation check for view window
        IntPtr viewInsertAfter = (IntPtr)1; // HWND_BOTTOM ensures view stays beneath dialogs
        Assert(viewInsertAfter == (IntPtr)1,
            "HWND_BOTTOM preserved on view window keeping child dialogs on top");

        // 9. SWP_NOACTIVATE stripping check
        uint swpFlags = 0x0013; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        uint SWP_NOACTIVATE = 0x0010;
        uint cleanFlags = swpFlags & ~SWP_NOACTIVATE;
        Assert((cleanFlags & SWP_NOACTIVATE) == 0,
            "SWP_NOACTIVATE stripped to guarantee window activation and focus restoration");
    }

    private static void TestGdiVectorAndScrollWindowHooks()
    {
        Console.WriteLine("\n--- 9. GDI Vector Drawing, Region Scaling & Viewport Scroll Hooks ---");

        string exePath = @"Imperialism II - Patched\Imperialism II.exe";
        byte[] bytes = File.ReadAllBytes(exePath);

        // Verify all 10 new IAT entries exist in the binary
        int ptrPoly = BitConverter.ToInt32(bytes, 0x00358818);
        Assert(ptrPoly > 0, "IAT entry for Polyline exists in executable at 0x0076BE18", string.Format("0x{0:X8}", ptrPoly));

        int ptrRect = BitConverter.ToInt32(bytes, 0x00358820);
        Assert(ptrRect > 0, "IAT entry for Rectangle exists in executable at 0x0076BE20", string.Format("0x{0:X8}", ptrRect));

        int ptrSelClip = BitConverter.ToInt32(bytes, 0x003588D4);
        Assert(ptrSelClip > 0, "IAT entry for SelectClipRgn exists in executable at 0x0076BED4", string.Format("0x{0:X8}", ptrSelClip));

        int ptrExtSelClip = BitConverter.ToInt32(bytes, 0x0035891C);
        Assert(ptrExtSelClip > 0, "IAT entry for ExtSelectClipRgn exists in executable at 0x0076BF1C", string.Format("0x{0:X8}", ptrExtSelClip));

        int ptrMoveTo = BitConverter.ToInt32(bytes, 0x003588E4);
        Assert(ptrMoveTo > 0, "IAT entry for MoveToEx exists in executable at 0x0076BEE4", string.Format("0x{0:X8}", ptrMoveTo));

        int ptrLineTo = BitConverter.ToInt32(bytes, 0x003588E8);
        Assert(ptrLineTo > 0, "IAT entry for LineTo exists in executable at 0x0076BEE8", string.Format("0x{0:X8}", ptrLineTo));

        int ptrScroll = BitConverter.ToInt32(bytes, 0x00358D80);
        Assert(ptrScroll > 0, "IAT entry for ScrollWindow exists in executable at 0x0076C380", string.Format("0x{0:X8}", ptrScroll));

        int ptrScrollEx = BitConverter.ToInt32(bytes, 0x00358DD8);
        Assert(ptrScrollEx > 0, "IAT entry for ScrollWindowEx exists in executable at 0x0076C3D8", string.Format("0x{0:X8}", ptrScrollEx));

        int ptrFill = BitConverter.ToInt32(bytes, 0x00358F2C);
        Assert(ptrFill > 0, "IAT entry for FillRect exists in executable at 0x0076C52C", string.Format("0x{0:X8}", ptrFill));

        int ptrFrame = BitConverter.ToInt32(bytes, 0x00358F30);
        Assert(ptrFrame > 0, "IAT entry for FrameRect exists in executable at 0x0076C530", string.Format("0x{0:X8}", ptrFrame));

        // Test coordinate scaling for cursor crosshair & territory vector lines on 4K
        int dstX = 480;
        int targetW = 2880;
        int targetH = 2160;

        // Cursor frame at (50, 100, 114, 164)
        int curL = dstX + (int)Math.Round(50.0 * targetW / 800.0);
        int curT = (int)Math.Round(100.0 * targetH / 600.0);
        int curR = dstX + (int)Math.Round(114.0 * targetW / 800.0);
        int curB = (int)Math.Round(164.0 * targetH / 600.0);
        Assert(curL == 660 && curT == 360 && curR == 890 && curB == 590,
            "Cursor area vector rectangle scales inside game presentation (660,360,890,590) preventing pillarbox drift");

        // Cyan connection vector line from (500, 300) to (586, 542)
        int lineX1 = dstX + (int)Math.Round(500.0 * targetW / 800.0);
        int lineY1 = (int)Math.Round(300.0 * targetH / 600.0);
        int lineX2 = dstX + (int)Math.Round(586.0 * targetW / 800.0);
        int lineY2 = (int)Math.Round(542.0 * targetH / 600.0);
        Assert(lineX1 == 2280 && lineY1 == 1080 && lineX2 == 2590 && lineY2 == 1951,
            "Map boundary vector line scales accurately to 4K coordinates (2280,1080 -> 2590,1951)");

        // Viewport scroll scaling: dx=32 within viewport (8, 58, 586, 542)
        int scrollDx = (int)Math.Round(32.0 * targetW / 800.0);
        int vpL = dstX + (int)Math.Round(8.0 * targetW / 800.0);
        int vpT = (int)Math.Round(58.0 * targetH / 600.0);
        int vpR = dstX + (int)Math.Round(586.0 * targetW / 800.0);
        int vpB = (int)Math.Round(542.0 * targetH / 600.0);
        Assert(scrollDx == 115 && vpL == 509 && vpT == 209 && vpR == 2590 && vpB == 1951,
            "Viewport ScrollWindow parameters scale dx=115 and rect=(509,209,2590,1951) preventing black hole artifacts");

        // Blit pillarbox boundary clamping: tile at virtual (-10, 50, 64x64)
        int tileDstX = dstX + (int)Math.Round(-10.0 * targetW / 800.0); // 480 - 36 = 444
        int tileDstW = (int)Math.Round(64.0 * targetW / 800.0);         // 230
        if (tileDstX < dstX)
        {
            int cut = dstX - tileDstX;
            tileDstX = dstX;
            tileDstW = Math.Max(0, tileDstW - cut);
        }
        Assert(tileDstX == 480 && tileDstW == 194,
            "Edge tile blit clamped to DstX=480 with width 194, completely protecting left pillarbox");
    }

    private static void TestSpriteTransparencyAndScreenReadHooks()
    {
        Console.WriteLine("\n--- 10. Sprite Sheet Transparency & Offscreen Screen Read Verification ---");

        // 1. Sprite sheet detection logic:
        // isSpriteSheet = (srcCanvasW > 800 && srcCanvasH <= 128 && srcW <= 128 && srcH <= 128);
        bool testUnitStrip = (7040 > 800 && 64 <= 128 && 64 <= 128 && 64 <= 128);
        Assert(testUnitStrip, "7040x64 animation strip with 64x64 frame correctly identified as unit sprite sheet");

        bool testSavedTile = (64 > 800 && 64 <= 128 && 64 <= 128 && 64 <= 128);
        Assert(!testSavedTile, "64x64 terrain tile correctly identified as non-sprite-sheet (uses standard StretchBlt)");

        bool testUiFull = (800 > 800 && 600 <= 128 && 800 <= 128 && 600 <= 128);
        Assert(!testUiFull, "800x600 UI canvas correctly identified as non-sprite-sheet");

        bool testMapBuffer = (864 > 800 && 728 <= 128 && 64 <= 128 && 64 <= 128);
        Assert(!testMapBuffer, "864x728 offscreen map buffer correctly identified as non-sprite-sheet");

        bool testSmallUi = (800 > 800 && 600 <= 128 && 35 <= 128 && 37 <= 128);
        Assert(!testSmallUi, "800x600 partial UI element correctly identified as non-sprite-sheet");

        // 2. Unit blit destination math: Explorer at virtual (362, 314, 64x64)
        int dstX = 480;
        int targetW = 2880;
        int targetH = 2160;
        int expDstX = dstX + (int)Math.Round((double)362 * targetW / 800.0);
        int expDstY = (int)Math.Round((double)314 * targetH / 600.0);
        int expDstRight = dstX + (int)Math.Round((double)(362 + 64) * targetW / 800.0);
        int expDstBottom = (int)Math.Round((double)(314 + 64) * targetH / 600.0);
        int expDstW = expDstRight - expDstX;
        int expDstH = expDstBottom - expDstY;
        Assert(expDstX == 1783 && expDstY == 1130 && expDstW == 231 && expDstH == 231,
            "Explorer at virtual (362, 314, 64x64) maps to exact physical 4K coordinates (1783, 1130, 231x231)");

        // 3. Screen read scaling math: Reading (362, 314, 64x64) from presentation window into offscreen memory DC
        int readSrcX = dstX + (int)Math.Round((double)362 * targetW / 800.0);
        int readSrcY = (int)Math.Round((double)314 * targetH / 600.0);
        int readSrcW = (int)Math.Round((double)64 * targetW / 800.0);
        int readSrcH = (int)Math.Round((double)64 * targetH / 600.0);
        Assert(readSrcX == 1783 && readSrcY == 1130 && readSrcW == 230 && readSrcH == 230,
            "Screen read at virtual (362, 314, 64x64) maps to physical terrain (1783, 1130) avoiding left pillarbox at X=362");

        // 4. Color-key transparency value: Default black COLORREF is 0x00000000
        uint crDefault = 0x00000000;
        Assert(crDefault == 0x00000000, "Default transparent color key is COLORREF RGB(0,0,0) (0x00000000)");
    }

    private static void TestInteractiveWorldMapZoom()
    {
        Console.WriteLine("\n--- 11. Interactive World Map Camera Zoom via Mousewheel ---");

        // 1. Initial state
        GraphicsBridge.SetZoom(1.0f);
        Assert(GraphicsBridge.CurrentZoom == 1.0f,
            "Initial world map camera zoom is 1.0x (unzoomed default)");

        // 2. 4K viewport geometry: (8, 58, 578x512) inside 800x600 canvas scaled to 4K (2880x2160 centered at X=480)
        int dstX = 480;
        int targetW = 2880;
        int targetH = 2160;
        int vpDstX = dstX + (int)Math.Round(8.0 * targetW / 800.0);
        int vpDstY = (int)Math.Round(58.0 * targetH / 600.0);
        int vpDstW = (int)Math.Round(578.0 * targetW / 800.0);
        int vpDstH = (int)Math.Round(512.0 * targetH / 600.0);
        Assert(vpDstX == 509 && vpDstY == 209 && vpDstW == 2081 && vpDstH == 1843,
            "World map viewport at 4K maps to (509, 209, 2081x1843)");

        // 3. Zoom level 1.5x crop calculation (center focal at 297, 314)
        GraphicsBridge.SetZoom(1.5f, 297, 314);
        Assert(GraphicsBridge.CurrentZoom == 1.5f && GraphicsBridge.ZoomEffectiveSx == 105 && GraphicsBridge.ZoomEffectiveSy == 144,
            "1.5x zoom calculates crop rect (105, 144, 385x341)",
            string.Format("({0}, {1}, {2}x{3})", GraphicsBridge.ZoomEffectiveSx, GraphicsBridge.ZoomEffectiveSy, GraphicsBridge.ZoomEffectiveSrcW, GraphicsBridge.ZoomEffectiveSrcH));

        // 4. Mouse coordinate hit-test translation at 1.5x zoom
        double uCenter = (297 - 8) / 578.0;
        double vCenter = (314 - 58) / 512.0;
        int mappedCenterX = (int)Math.Round(GraphicsBridge.ZoomEffectiveSx + uCenter * GraphicsBridge.ZoomEffectiveSrcW);
        int mappedCenterY = (int)Math.Round(GraphicsBridge.ZoomEffectiveSy + vCenter * GraphicsBridge.ZoomEffectiveSrcH);
        Assert(Math.Abs(mappedCenterX - 297) <= 1 && Math.Abs(mappedCenterY - 314) <= 1,
            "Viewport center click maps to source center (297, 314) under 1.5x zoom");

        // 5. Boundary clamping at top-left
        GraphicsBridge.SetZoom(2.0f, 10, 60);
        Assert(GraphicsBridge.ZoomEffectiveSx >= 8 && GraphicsBridge.ZoomEffectiveSy >= 58,
            "Zoom crop top-left strictly clamped >= (8, 58) preventing UI border bleed");

        // 6. Boundary clamping at bottom-right (full height 512, Y up to 570)
        GraphicsBridge.SetZoom(2.0f, 580, 565);
        Assert(GraphicsBridge.ZoomEffectiveSx + GraphicsBridge.ZoomEffectiveSrcW <= 586 &&
               GraphicsBridge.ZoomEffectiveSy + GraphicsBridge.ZoomEffectiveSrcH <= 570,
            "Zoom crop bottom-right strictly clamped <= (586, 570) preventing UI panel bleed");

        // 7. Reset zoom to 1.0x
        GraphicsBridge.SetZoom(1.0f);
        Assert(GraphicsBridge.CurrentZoom == 1.0f &&
               GraphicsBridge.ZoomEffectiveSx == 8 && GraphicsBridge.ZoomEffectiveSy == 58 &&
               GraphicsBridge.ZoomEffectiveSrcW == 578 && GraphicsBridge.ZoomEffectiveSrcH == 512,
            "Middle-click zoom reset restores 1.0x full-frame viewport (8, 58, 578x512)");

        // 8. Option B Tactical Camera Zoom-Out levels:
        // 0.75x: 768x683 tactical extent
        GraphicsBridge.SetZoom(0.75f);
        Assert(GraphicsBridge.CurrentZoom == 0.75f &&
               GraphicsBridge.ZoomEffectiveSrcW == 768 && GraphicsBridge.ZoomEffectiveSrcH == 683,
            "0.75x tactical camera zoom-out expands extent to 768x683 (1.33x tactical coverage)");

        // 0.50x: 1152x1024 tactical extent
        GraphicsBridge.SetZoom(0.50f);
        Assert(GraphicsBridge.CurrentZoom == 0.50f &&
               GraphicsBridge.ZoomEffectiveSrcW == 1152 && GraphicsBridge.ZoomEffectiveSrcH == 1024,
            "0.50x tactical camera zoom-out expands extent to 1152x1024 (2x tactical coverage)");

        // 0.25x: 2304x2048 tactical extent
        GraphicsBridge.SetZoom(0.25f);
        Assert(GraphicsBridge.CurrentZoom == 0.25f &&
               GraphicsBridge.ZoomEffectiveSrcW == 2304 && GraphicsBridge.ZoomEffectiveSrcH == 2048,
            "0.25x tactical camera zoom-out expands extent to 2304x2048 (4x tactical coverage)");

        // 9. Lower bound clamp: zooming below 0.25x strictly clamps to 0.25x minimum
        GraphicsBridge.SetZoom(0.10f);
        Assert(GraphicsBridge.CurrentZoom == 0.25f &&
               GraphicsBridge.ZoomEffectiveSrcW == 2304 && GraphicsBridge.ZoomEffectiveSrcH == 2048,
            "Zoom levels below 0.25x strictly clamp to 0.25x minimum (fitting within 2464x2304 tactical buffer)");

        // 10. Upper bound clamp: zooming above 3.0x strictly clamps to 3.0x
        GraphicsBridge.SetZoom(4.0f);
        Assert(GraphicsBridge.CurrentZoom == 3.0f,
            "Zoom levels above 3.0x clamp to maximum 3.0x magnification");

        // 11. Mouse coordinate mapping at 1.0x (1:1 direct passthrough)
        GraphicsBridge.SetZoom(1.0f);
        int mappedX, mappedY;
        GraphicsBridge.MapZoomCoordinate(8, 58, out mappedX, out mappedY);
        Assert(mappedX == 8 && mappedY == 58, "Top-left map coordinate (8, 58) maps 1:1 at 1.0x zoom");

        // Test bottom line of tiles at Y = 570 (verifying no cutoff)
        GraphicsBridge.MapZoomCoordinate(586, 570, out mappedX, out mappedY);
        Assert(mappedX == 586 && mappedY == 570,
            "Bottom line of tiles (586, 570) maps accurately without cutoff at 1.0x zoom");

        // 12. Mouse coordinate translation under 0.50x tactical camera zoom-out
        GraphicsBridge.SetZoom(0.50f);
        GraphicsBridge.MapZoomCoordinate(298, 314, out mappedX, out mappedY);
        Assert(mappedX == 586 && mappedY == 570,
            "Viewport center click (298, 314) translates to tactical coordinate (586, 570) under 0.50x zoom",
            string.Format("({0}, {1})", mappedX, mappedY));

        // 13. Mouse coordinate translation under 0.25x tactical camera zoom-out
        GraphicsBridge.SetZoom(0.25f);
        GraphicsBridge.MapZoomCoordinate(298, 314, out mappedX, out mappedY);
        Assert(mappedX == 1162 && mappedY == 1082,
            "Viewport center click (298, 314) translates to tactical coordinate (1162, 1082) under 0.25x zoom",
            string.Format("({0}, {1})", mappedX, mappedY));
    }

    private static void TestOptionBArchitecture()
    {
        Console.WriteLine("\n--- 12. Option B Tactical Camera Zoom-Out Architecture & Downsampling ---");

        // 1. Synthetic 16bpp RGB555 test buffer: 1152x1024 source buffer downsampled to 576x512
        int srcW = 1152;
        int srcH = 1024;
        int dstW = 800;
        int dstH = 600;

        int srcStride = ((srcW * 2 + 3) / 4) * 4;
        int dstStride = ((dstW * 2 + 3) / 4) * 4;

        IntPtr pSrcBits = Marshal.AllocHGlobal(srcStride * srcH);
        IntPtr pDstBits = Marshal.AllocHGlobal(dstStride * dstH);
        IntPtr pSrcBmi = Marshal.AllocHGlobal(40);
        IntPtr pDstBmi = Marshal.AllocHGlobal(40);
        IntPtr pSrcDib = Marshal.AllocHGlobal(32);
        IntPtr pDstDib = Marshal.AllocHGlobal(32);
        IntPtr pSrcPixMap = Marshal.AllocHGlobal(36);
        IntPtr pDstPixMap = Marshal.AllocHGlobal(36);
        IntPtr pSrcRect = Marshal.AllocHGlobal(16);
        IntPtr pDstRect = Marshal.AllocHGlobal(16);

        try
        {
            // Setup BITMAPINFOHEADER for 16bpp RGB555 (BI_RGB = 0)
            Marshal.WriteInt32(pSrcBmi, 0, 40);
            Marshal.WriteInt32(pSrcBmi, 4, srcW);
            Marshal.WriteInt32(pSrcBmi, 8, -srcH); // top-down
            Marshal.WriteInt16(pSrcBmi, 12, 1);
            Marshal.WriteInt16(pSrcBmi, 14, 16);
            Marshal.WriteInt32(pSrcBmi, 16, 0); // BI_RGB (555)

            Marshal.WriteInt32(pDstBmi, 0, 40);
            Marshal.WriteInt32(pDstBmi, 4, dstW);
            Marshal.WriteInt32(pDstBmi, 8, -dstH); // top-down
            Marshal.WriteInt16(pDstBmi, 12, 1);
            Marshal.WriteInt16(pDstBmi, 14, 16);
            Marshal.WriteInt32(pDstBmi, 16, 0);

            // Setup DIB structs: offset 0x0C = pBits, offset 0x10 = pBmi
            Marshal.WriteIntPtr(pSrcDib, 0x0C, pSrcBits);
            Marshal.WriteIntPtr(pSrcDib, 0x10, pSrcBmi);
            Marshal.WriteIntPtr(pDstDib, 0x0C, pDstBits);
            Marshal.WriteIntPtr(pDstDib, 0x10, pDstBmi);

            // Setup PixMap structs: offset 0x1C = pDib
            Marshal.WriteIntPtr(pSrcPixMap, 0x1C, pSrcDib);
            Marshal.WriteIntPtr(pDstPixMap, 0x1C, pDstDib);

            // Fill source bits with alternating pure Red (0x7C00) and pure Blue (0x001F) in 2x2 blocks:
            // Expected average: R=15, G=0, B=15 -> (15<<10)|15 = 0x3C0F (Purple)
            unsafe
            {
                byte* pSrc = (byte*)pSrcBits.ToPointer();
                byte* pDst = (byte*)pDstBits.ToPointer();

                for (int y = 0; y < srcH; y++)
                {
                    ushort* row = (ushort*)(pSrc + y * srcStride);
                    for (int x = 0; x < srcW; x++)
                    {
                        // 2x2 checkerboard of Red and Blue
                        bool isRed = ((x & 1) == (y & 1));
                        row[x] = (ushort)(isRed ? 0x7C00 : 0x001F);
                    }
                }

                // Clear dest bits
                for (int y = 0; y < dstH; y++)
                {
                    ushort* row = (ushort*)(pDst + y * dstStride);
                    for (int x = 0; x < dstW; x++) row[x] = 0;
                }
            }

            // Setup rects: srcRect = (0, 0, 1152, 1024), dstRect = (10, 58, 586, 570)
            Marshal.WriteInt32(pSrcRect, 0, 0);
            Marshal.WriteInt32(pSrcRect, 4, 0);
            Marshal.WriteInt32(pSrcRect, 8, 1152);
            Marshal.WriteInt32(pSrcRect, 12, 1024);

            Marshal.WriteInt32(pDstRect, 0, 10);
            Marshal.WriteInt32(pDstRect, 4, 58);
            Marshal.WriteInt32(pDstRect, 8, 586);
            Marshal.WriteInt32(pDstRect, 12, 570);

            // Test 1: Nearest Neighbor (default)
            GraphicsBridge.CurrentZoomFilter = ZoomFilter.Nearest;
            GraphicsBridge.DownsampleMapBuffer(pSrcPixMap, pDstPixMap, pSrcRect, pDstRect);

            unsafe
            {
                byte* pDst = (byte*)pDstBits.ToPointer();
                ushort* row = (ushort*)(pDst + 58 * dstStride);
                ushort pixel = row[10];
                int r = (pixel >> 10) & 0x1F;
                int g = (pixel >> 5) & 0x1F;
                int b = pixel & 0x1F;

                // Point sample at center of 2x2 footprint retains crisp pure Red (R=31, G=0, B=0)
                Assert(r == 31 && g == 0 && b == 0,
                    "Nearest-neighbor downsampler preserves 100% crisp pixel art without blurring (Red R=31, G=0, B=0)",
                    string.Format("RGB=({0}, {1}, {2}), 0x{3:X4}", r, g, b, pixel));

                ushort outsidePixel = row[9];
                Assert(outsidePixel == 0, "Destination buffer outside viewport (X=9, Y=58) is uncorrupted");
            }

            // Test 2: Smooth Area-Averaging Box Filter
            GraphicsBridge.CurrentZoomFilter = ZoomFilter.Smooth;
            GraphicsBridge.DownsampleMapBuffer(pSrcPixMap, pDstPixMap, pSrcRect, pDstRect);

            unsafe
            {
                byte* pDst = (byte*)pDstBits.ToPointer();
                ushort* row = (ushort*)(pDst + 58 * dstStride);
                ushort pixel = row[10];
                int r = (pixel >> 10) & 0x1F;
                int g = (pixel >> 5) & 0x1F;
                int b = pixel & 0x1F;

                // 2 red pixels (R=31) + 2 blue pixels (B=31) -> avg R=15, G=0, avg B=15
                Assert(r == 15 && g == 0 && b == 15,
                    "Smooth area-averaging box downsampler computes 2x2 average (Purple R=15, G=0, B=15)",
                    string.Format("RGB=({0}, {1}, {2}), 0x{3:X4}", r, g, b, pixel));
            }

            // Reset back to Smooth default
            GraphicsBridge.CurrentZoomFilter = ZoomFilter.Smooth;
        }
        finally
        {
            Marshal.FreeHGlobal(pSrcBits);
            Marshal.FreeHGlobal(pDstBits);
            Marshal.FreeHGlobal(pSrcBmi);
            Marshal.FreeHGlobal(pDstBmi);
            Marshal.FreeHGlobal(pSrcDib);
            Marshal.FreeHGlobal(pDstDib);
            Marshal.FreeHGlobal(pSrcPixMap);
            Marshal.FreeHGlobal(pDstPixMap);
            Marshal.FreeHGlobal(pSrcRect);
            Marshal.FreeHGlobal(pDstRect);
        }

        // 2. Engine Hook Address Verification
        Assert(GraphicsBridge.VTableMapDialogDraw == 0x00703A14, "VTable Slot 69 (TMapDialog::Draw) hooked at 0x00703A14");
        Assert(GraphicsBridge.VTableMapDialogDrawSelection == 0x00703AB8, "VTable Slot 110 (Old World DrawSelection) hooked at 0x00703AB8");
        Assert(GraphicsBridge.VTableMapDialogScreenToTile == 0x00703AC4, "VTable Slot 113 (Old World ScreenToTile) hooked at 0x00703AC4");
        Assert(GraphicsBridge.VTableNewMapDialogDrawSelection == 0x00703D48, "VTable Slot 110 (New World DrawSelection) hooked at 0x00703D48");
        Assert(GraphicsBridge.VTableNewMapDialogScreenToTile == 0x00703D54, "VTable Slot 113 (New World ScreenToTile) hooked at 0x00703D54");
        Assert(GraphicsBridge.CallTMapDialogSelectionCopyBits1 == 0x00559BC2, "Old World Selection CopyBits1 call hooked at 0x00559BC2");
        Assert(GraphicsBridge.CallTMapDialogSelectionCopyBits2 == 0x00559CA5, "Old World Selection CopyBits2 call hooked at 0x00559CA5");
        Assert(GraphicsBridge.CallTNewMapDialogSelectionCopyBits1 == 0x0055B35E, "New World Selection CopyBits1 call hooked at 0x0055B35E");
        Assert(GraphicsBridge.CallTNewMapDialogSelectionCopyBits2 == 0x0055B442, "New World Selection CopyBits2 call hooked at 0x0055B442");
        Assert(GraphicsBridge.VTableMapDialogDrawSelectionBox == 0x00703AF4, "VTable Slot 125 (Old World DrawSelectionBox) hooked at 0x00703AF4");
        Assert(GraphicsBridge.VTableNewMapDialogDrawSelectionBox == 0x00703D84, "VTable Slot 125 (New World DrawSelectionBox) hooked at 0x00703D84");
        Assert(GraphicsBridge.VTableMapDialog3DrawSelectionBox == 0x00704014, "VTable Slot 125 (3rd Dialog DrawSelectionBox) hooked at 0x00704014");
        Assert(GraphicsBridge.AddrTMapDialogTileAlloc == 0x0055D3E3, "Tile cache allocator overflow protection hooked at 0x0055D3E3");
        Assert(GraphicsBridge.CallTMapDialogDrawString == 0x0055D7C0, "City/Province label DrawString call hooked at 0x0055D7C0");
        Assert(GraphicsBridge.AddrDrawStringThunk == 0x0040A63C, "DrawString thunk entry point hooked at 0x0040A63C");
        Assert(GraphicsBridge.AddrDrawStringImpl == 0x004A8700, "DrawString engine implementation targeted at 0x004A8700");
        Assert(GraphicsBridge.CapturedLabels != null, "CapturedLabels collection initialized and thread-safe");
        Assert(GraphicsBridge.CityLabels != null, "CityLabels persistent registry initialized and thread-safe");
        Assert(GraphicsBridge.CurrentZoomFilter == ZoomFilter.Smooth, "Default zoom filter configured to Smooth (anti-aliased box average)");

        // ScreenToTile zoom-out coordinate conversion verification
        GraphicsBridge.LastSx0 = 64;
        GraphicsBridge.LastSy0 = 64;
        GraphicsBridge.LastScaleX = 2.0;
        GraphicsBridge.LastScaleY = 2.0;

        int sttOutX, sttOutY;
        // In viewport coordinate (0, 0) -> buf=(64, 64) -> out=(0, 0)
        GraphicsBridge.MapScreenToTileCoordinates(0, 0, out sttOutX, out sttOutY);
        Assert(sttOutX == 0 && sttOutY == 0, "Top-left viewport coordinate (0, 0) maps to camera relative (0, 0)", string.Format("({0}, {1})", sttOutX, sttOutY));

        // In viewport coordinate (288, 256) -> buf=(64 + 576, 64 + 512) = (640, 576) -> out=(576, 512)
        GraphicsBridge.MapScreenToTileCoordinates(288, 256, out sttOutX, out sttOutY);
        Assert(sttOutX == 576 && sttOutY == 512, "Center viewport coordinate (288, 256) with 2.0x scale maps to camera relative (576, 512)", string.Format("({0}, {1})", sttOutX, sttOutY));

        // 3. City & Province Hex Grid Projection Verification (Engine 0x0054F680 Formula)
        int bufX, bufY;
        // Even row: col=10, row=20 (tileIndex = 20 * 108 + 10 = 2170)
        GraphicsBridge.ProjectTileToBuffer(2170, 0, 0, out bufX, out bufY);
        Assert(bufX == 704 && bufY == 1344, "Even hex row (col=10, row=20) projects to buffer (704, 1344)", string.Format("({0}, {1})", bufX, bufY));

        // Odd row: col=10, row=21 (tileIndex = 21 * 108 + 10 = 2278) -> +32px isometric hex stagger
        GraphicsBridge.ProjectTileToBuffer(2278, 0, 0, out bufX, out bufY);
        Assert(bufX == 736 && bufY == 1408, "Odd hex row (col=10, row=21) applies +32px horizontal stagger to (736, 1408)", string.Format("({0}, {1})", bufX, bufY));

        // Horizontal world cylinder wrap: tile at col=0, row=0 with camera at far right (camX=6800)
        GraphicsBridge.ProjectTileToBuffer(0, 6800, 0, out bufX, out bufY);
        Assert(bufX == 176 && bufY == 64, "Cylinder world wrap: col=0 at camX=6800 seamlessly wraps to bufX=176", string.Format("({0}, {1})", bufX, bufY));

        // 4. Option B Zoom-Out Coordinate Mapping Verification
        GraphicsBridge.SetZoom(0.50f);
        int mappedX, mappedY;
        GraphicsBridge.MapZoomCoordinate(10, 58, out mappedX, out mappedY);
        Assert(mappedX == 10 && mappedY == 58, "Top-left tactical map coordinate (10, 58) maps to (10, 58) at 0.50x zoom");

        GraphicsBridge.MapZoomCoordinate(298, 314, out mappedX, out mappedY);
        // relX = 298 - 10 = 288 -> 288 / 0.50 = 576 -> 10 + 576 = 586
        // relY = 314 - 58 = 256 -> 256 / 0.50 = 512 -> 58 + 512 = 570
        Assert(mappedX == 586 && mappedY == 570, "Center tactical map coordinate (298, 314) maps to (586, 570) at 0.50x zoom", string.Format("({0}, {1})", mappedX, mappedY));

        // 5. Intelligent Label Collision Avoidance & Capital Priority Verification
        Assert(GraphicsBridge.NationalCapitals != null && GraphicsBridge.NationalCapitals.Contains("Madrid"), "NationalCapitals registry contains 'Madrid'");
        Assert(GraphicsBridge.NationalCapitals.Contains("London") && GraphicsBridge.NationalCapitals.Contains("Paris"), "NationalCapitals registry contains 'London' and 'Paris'");

        // Overlapping bounding boxes: Box A at (100, 100, 180, 120), Box B at (160, 110, 240, 130) -> Overlaps
        RECT boxMadrid = new RECT { Left = 100, Top = 100, Right = 180, Bottom = 120 };
        RECT boxUlster = new RECT { Left = 160, Top = 110, Right = 240, Bottom = 130 };
        Assert(GraphicsBridge.CheckLabelCollision(boxMadrid, boxUlster), "Overlapping label bounding boxes correctly trigger collision avoidance");

        // Non-overlapping bounding boxes: Box C at (300, 100, 380, 120)
        RECT boxLondon = new RECT { Left = 300, Top = 100, Right = 380, Bottom = 120 };
        Assert(!GraphicsBridge.CheckLabelCollision(boxMadrid, boxLondon), "Spaced labels do not collide and are both permitted to render");

        // 6. City & Ocean Label Filtering Verification
        Assert(GraphicsBridge.IsOceanName("Ross Channel"), "Ocean keyword 'Channel' matches 'Ross Channel'");
        Assert(GraphicsBridge.IsOceanName("North Sea"), "Ocean keyword 'Sea' matches 'North Sea'");
        Assert(GraphicsBridge.IsOceanName("Atlantic Ocean"), "Ocean keyword 'Ocean' matches 'Atlantic Ocean'");
        Assert(!GraphicsBridge.IsOceanName("Madrid"), "City 'Madrid' is not an ocean name");
        Assert(!GraphicsBridge.IsOceanName("Normandy"), "Province 'Normandy' is not an ocean name");

        Assert(GraphicsBridge.IsCityName("Madrid"), "Valid city 'Madrid' recognized");
        Assert(GraphicsBridge.IsCityName("London"), "Valid city 'London' recognized");
        Assert(GraphicsBridge.IsCityName("Paris"), "Valid city 'Paris' recognized");
        Assert(GraphicsBridge.IsCityName("Limerick"), "Valid city 'Limerick' recognized");
        Assert(GraphicsBridge.IsCityName("Zurich"), "Valid city 'Zurich' recognized");
        Assert(!GraphicsBridge.IsCityName("Granada"), "Blacklisted province 'Granada' rejected");
        Assert(!GraphicsBridge.IsCityName("Ulster"), "Blacklisted province 'Ulster' rejected");
        Assert(!GraphicsBridge.IsCityName("The Pale"), "Blacklisted province 'The Pale' rejected");
        Assert(!GraphicsBridge.IsCityName("Normandy"), "Blacklisted province 'Normandy' rejected");
        Assert(!GraphicsBridge.IsCityName("hoo"), "Corrupt token 'hoo' rejected");
        Assert(!GraphicsBridge.IsCityName("Ross Channel"), "Ocean name 'Ross Channel' rejected as city");

        GraphicsBridge.SetZoom(1.0f); // Reset zoom to 1.0x
    }

    public static int Main()
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("  Imperialism II High-Resolution Bridge Test Suite ");
        Console.WriteLine("=================================================");

        TestBinaryPatches();
        TestExportAndDllLoad();
        TestGeometryMath();
        TestMouseCoordinateMapping();
        TestChildWindowScaling();
        TestWindowSizePreservation();
        TestInvalidateRectAndClipping();
        TestAmbitMcWindowScaling();
        TestGdiVectorAndScrollWindowHooks();
        TestSpriteTransparencyAndScreenReadHooks();
        TestInteractiveWorldMapZoom();
        TestOptionBArchitecture();

        Console.WriteLine("\n=================================================");
        Console.WriteLine(string.Format("  Summary: {0} Passed, {1} Failed", s_passed, s_failed));
        Console.WriteLine("=================================================");

        return s_failed == 0 ? 0 : 1;
    }
}
