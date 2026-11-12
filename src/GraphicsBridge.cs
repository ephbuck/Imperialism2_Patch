using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Imp2Net
{
    public enum DisplayMode
    {
        Fullscreen = 0,
        Windowed = 1
    }

    public enum AspectScale
    {
        Fit = 0,     // 4:3 maintained with side pillarboxes
        Stretch = 1  // Fill entire display width
    }

    public enum ZoomFilter
    {
        Nearest = 0, // 100% crisp pixel art, zero blur (recommended)
        Sharp   = 1, // Enhanced contrast with anti-aliasing
        Smooth  = 2  // Area-averaging box blur
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// High-Resolution Presentation Bridge for Imperialism II.
    /// Intercepts Win32 GDI presentation calls (BitBlt, StretchDIBits), window creation,
    /// geometry queries (GetClientRect, ScreenToClient), and window messaging.
    /// Automatically upscales 800x600 rendering to native monitor resolutions (4K, 1440p, 1080p)
    /// with bicubic/halftone anti-aliasing, perfect 4:3 aspect ratio, and 1:1 mouse tracking.
    /// Also supports interactive camera zoom on the world map viewport (576x512).
    /// </summary>
    public static class GraphicsBridge
    {
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const int GWL_WNDPROC = -4;
        private const int GWL_STYLE = -16;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_CLIPSIBLINGS = 0x04000000;
        private const uint WS_CLIPCHILDREN = 0x02000000;
        private const uint WS_EX_APPWINDOW = 0x00040000;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SRCCOPY = 0x00CC0020;
        private const int HALFTONE = 4;
        private const int COLORONCOLOR = 3;
        private const int BLACK_BRUSH = 4;
        private const int OBJ_BITMAP = 7;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        // Native IAT addresses in Imperialism II.exe (base 0x00400000)
        private const int IAT_POLYLINE          = 0x0076BE18;
        private const int IAT_RECTANGLE         = 0x0076BE20;
        private const int IAT_STRETCHDIBITS     = 0x0076BDE0;
        private const int IAT_BITBLT            = 0x0076BDEC;
        private const int IAT_GETCLIPBOX        = 0x0076BED0;
        private const int IAT_SELECTCLIPRGN      = 0x0076BED4;
        private const int IAT_EXCLUDECLIPRECT   = 0x0076BED8;
        private const int IAT_INTERSECTCLIPRECT = 0x0076BEDC;
        private const int IAT_MOVETOEX          = 0x0076BEE4;
        private const int IAT_LINETO            = 0x0076BEE8;
        private const int IAT_EXTSELECTCLIPRGN  = 0x0076BF1C;
        private const int IAT_SCROLLWINDOW      = 0x0076C380;
        private const int IAT_SCROLLWINDOWEX    = 0x0076C3D8;
        private const int IAT_DEFERWINDOWPOS    = 0x0076C3A0;
        private const int IAT_MOVEWINDOW        = 0x0076C3E4;
        private const int IAT_CLIENTTOSCREEN    = 0x0076C42C;
        private const int IAT_CREATEWINDOWEXA   = 0x0076C480;
        private const int IAT_BEGINPAINT        = 0x0076C4DC;
        private const int IAT_FILLRECT          = 0x0076C52C;
        private const int IAT_FRAMERECT         = 0x0076C530;
        private const int IAT_VALIDATERECT      = 0x0076C548;
        private const int IAT_SCREENTOCLIENT    = 0x0076C598;
        private const int IAT_INVALIDATERECT    = 0x0076C5CC;
        private const int IAT_GETCLIENTRECT     = 0x0076C5D8;
        private const int IAT_SETWINDOWPOS      = 0x0076C5F4;

        // Option B Tactical Camera Engine Addresses
        private const int VTABLE_TMAPDIALOG_DRAW            = 0x00703A14; // Slot 69 (Old World)
        private const int VTABLE_TMAPDIALOG_DRAW_SELECTION  = 0x00703AB8; // Slot 110 (Old World)
        private const int VTABLE_TMAPDIALOG_SCREEN_TO_TILE  = 0x00703AC4; // Slot 113 (Old World)

        private const int VTABLE_TNEWMAPDIALOG_DRAW         = 0x00703CA4; // Slot 69 (New World)
        private const int VTABLE_TNEWMAPDIALOG_DRAW_SELECTION = 0x00703D48; // Slot 110 (New World)
        private const int VTABLE_TNEWMAPDIALOG_SCREEN_TO_TILE = 0x00703D54; // Slot 113 (New World)

        private const int VTABLE_TMAPDIALOG3_DRAW           = 0x00703F34; // Slot 69 (3rd dialog)
        private const int VTABLE_TMAPDIALOG3_DRAW_SELECTION = 0x00703FD8; // Slot 110 (3rd dialog)
        private const int VTABLE_TMAPDIALOG3_SCREEN_TO_TILE = 0x00703FE4; // Slot 113 (3rd dialog)

        // Option B Selection & Sprite Preservation Addresses
        private const int CALL_TMAPDIALOG_SELECTION_COPYBITS1    = 0x00559BC2; // Slot 110 selection CopyBits call (Old World)
        private const int CALL_TMAPDIALOG_SELECTION_COPYBITS2    = 0x00559CA5; // Slot 110 restore CopyBits call (Old World)
        private const int CALL_TNEWMAPDIALOG_SELECTION_COPYBITS1 = 0x0055B35E; // Slot 110 selection CopyBits call (New World)
        private const int CALL_TNEWMAPDIALOG_SELECTION_COPYBITS2 = 0x0055B442; // Slot 110 restore CopyBits call (New World)

        private const int VTABLE_TMAPDIALOG_DRAW_SELECTION_BOX    = 0x00703AF4; // Slot 125 DrawSelectionBox (Old World)
        private const int VTABLE_TNEWMAPDIALOG_DRAW_SELECTION_BOX = 0x00703D84; // Slot 125 DrawSelectionBox (New World)
        private const int VTABLE_TMAPDIALOG3_DRAW_SELECTION_BOX   = 0x00704014; // Slot 125 DrawSelectionBox (3rd dialog)

        private const int ADDR_TMAPDIALOG_TILE_ALLOC     = 0x0055D3E3; // Tile cache slot allocator bounds check
        private const int CALL_TMAPDIALOG_COPYBITS       = 0x0055D889; // 5-byte call to CopyBits
        private const int ADDR_ORIG_COPYBITS             = 0x00404E8F; // CopyBits entry point
        private const int CALL_TMAPDIALOG_DRAWSTRING     = 0x0055D7C0; // 5-byte call to DrawString in TMapDialog
        private const int ADDR_ORIG_DRAWSTRING           = 0x0040A63C; // DrawString thunk entry point
        private const int ADDR_DRAWSTRING_IMPL           = 0x004A8700; // DrawString core engine implementation
        private const int CODE_UPDATETHEGWORLD_RECT      = 0x00532EAC; // MakeNewGWorld bounds setup

        [DllImport("ole32.dll", SetLastError = true)]
        private static extern int CoInitialize(IntPtr pvReserved);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandleA(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern uint GetModuleFileNameA(IntPtr hModule, StringBuilder lpFilename, uint nSize);

        private const uint MEM_COMMIT  = 0x1000;
        private const uint MEM_RESERVE = 0x2000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr WindowFromDC(IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr CreateWindowExA(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongA(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetWindowLongA(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallWindowProcA(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr DefWindowProcA(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageA(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessageA(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int cx, int cy);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int SetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpBits, IntPtr lpbmi, uint ColorUse);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hGdiObj);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr CreateFontA(
            int nHeight, int nWidth, int nEscapement, int nOrientation, int fnWeight,
            uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut, uint fdwCharSet,
            uint fdwOutputPrecision, uint fdwClipPrecision, uint fdwQuality,
            uint fdwPitchAndFamily, string lpszFace);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int SetBkMode(IntPtr hdc, int iBkMode);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern uint SetTextColor(IntPtr hdc, uint crColor);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern uint SetTextAlign(IntPtr hdc, uint fMode);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern bool TextOutA(IntPtr hdc, int nXStart, int nYStart, string lpString, int cbString);

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
        }

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern bool GetTextExtentPoint32A(IntPtr hdc, string lpString, int cbString, out SIZE lpSize);

        private const int TRANSPARENT = 1;
        private const uint TA_LEFT = 0;
        private const uint TA_CENTER = 6;
        private const uint TA_BASELINE = 24;

        private const uint WM_SETFONT = 0x0030;

        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XFORM
        {
            public float eM11;
            public float eM12;
            public float eM21;
            public float eM22;
            public float eDx;
            public float eDy;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int FrameRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int GetClipBox(IntPtr hdc, out RECT lprect);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int ExcludeClipRect(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr GetStockObject(int fnObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int SelectClipRgn(IntPtr hdc, IntPtr hrgn);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int ExtSelectClipRgn(IntPtr hdc, IntPtr hrgn, int fnMode);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int GetRgnBox(IntPtr hrgn, out RECT lprc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern uint GetRegionData(IntPtr hrgn, uint dwCount, IntPtr lpRgnData);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr ExtCreateRegion(ref XFORM lpXform, uint nCount, IntPtr lpRgnData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ScrollWindow(IntPtr hWnd, int XAmount, int YAmount, IntPtr lpRect, IntPtr lpClipRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ScrollWindowEx(IntPtr hWnd, int dx, int dy, IntPtr prcScroll, IntPtr prcClip, IntPtr hrgnUpdate, IntPtr prcUpdate, uint flags);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool MoveToEx(IntPtr hdc, int X, int Y, IntPtr lpPoint);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool LineTo(IntPtr hdc, int nXEnd, int nYEnd);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool Polyline(IntPtr hdc, IntPtr lppt, int cPoints);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool Rectangle(IntPtr hdc, int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool StretchBlt(IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest, IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc, uint dwRop);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int SetStretchBltMode(IntPtr hdc, int iStretchMode);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool SetBrushOrgEx(IntPtr hdc, int nXOrg, int nYOrg, IntPtr lppt);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentObject(IntPtr hdc, int uObjectType);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

        [DllImport("msimg32.dll", SetLastError = true)]
        private static extern bool TransparentBlt(
            IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
            IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint crTransparent);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int StretchDIBits(
            IntPtr hdc, int xDest, int yDest, int DestWidth, int DestHeight,
            int xSrc, int ySrc, int SrcWidth, int SrcHeight,
            IntPtr lpBits, IntPtr lpbmi, uint iUsage, uint rop);

        // Native delegates
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool BitBltDelegate(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int StretchDIBitsDelegate(
            IntPtr hdc, int xDest, int yDest, int DestWidth, int DestHeight,
            int xSrc, int ySrc, int SrcWidth, int SrcHeight,
            IntPtr lpBits, IntPtr lpbmi, uint iUsage, uint rop);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate IntPtr CreateWindowExDelegate(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool GetClientRectDelegate(IntPtr hWnd, out RECT lpRect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool ScreenToClientDelegate(IntPtr hWnd, ref POINT lpPoint);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool ClientToScreenDelegate(IntPtr hWnd, ref POINT lpPoint);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool MoveWindowDelegate(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool SetWindowPosDelegate(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate IntPtr DeferWindowPosDelegate(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool InvalidateRectDelegate(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool ValidateRectDelegate(IntPtr hWnd, IntPtr lpRect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate IntPtr BeginPaintDelegate(IntPtr hWnd, out PAINTSTRUCT lpPaint);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetClipBoxDelegate(IntPtr hdc, out RECT lprect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int IntersectClipRectDelegate(IntPtr hdc, int left, int top, int right, int bottom);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int ExcludeClipRectDelegate(IntPtr hdc, int left, int top, int right, int bottom);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool MoveToExDelegate(IntPtr hdc, int X, int Y, IntPtr lpPoint);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool LineToDelegate(IntPtr hdc, int nXEnd, int nYEnd);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool PolylineDelegate(IntPtr hdc, IntPtr lppt, int cPoints);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool RectangleDelegate(IntPtr hdc, int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int SelectClipRgnDelegate(IntPtr hdc, IntPtr hrgn);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int ExtSelectClipRgnDelegate(IntPtr hdc, IntPtr hrgn, int fnMode);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate bool ScrollWindowDelegate(IntPtr hWnd, int XAmount, int YAmount, IntPtr lpRect, IntPtr lpClipRect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int ScrollWindowExDelegate(IntPtr hWnd, int dx, int dy, IntPtr prcScroll, IntPtr prcClip, IntPtr hrgnUpdate, IntPtr prcUpdate, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int FillRectDelegate(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int FrameRectDelegate(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate void MapDialogDrawDelegate(IntPtr thisPtr, IntPtr pRect);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate int MapDialogConvertPointDelegate(IntPtr thisPtr, IntPtr pPt);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate void MapDrawSelectionDelegate(IntPtr thisPtr);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate void MapDrawSelectionBoxDelegate(IntPtr thisPtr, IntPtr pArray);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        public delegate void MapScreenToTileDelegate(IntPtr thisPtr, IntPtr pPt, IntPtr pOutCol, IntPtr pOutRow, IntPtr pOutValid);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void MapCopyBitsDelegate(IntPtr srcPixMap, IntPtr dstPixMap, IntPtr pSrcRect, IntPtr pDstRect, int mode, IntPtr maskRgn);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void MapDrawStringDelegate(IntPtr pCString);

        // Option B Captured Map Labels for High-Resolution Vector Overlay
        public struct CapturedLabel
        {
            public string Text;
            public int PenX;
            public int PenY;
        }

        public struct CityLabelRecord
        {
            public int TileIndex;
            public string Text;
            public int LocalX;
            public int LocalY;
        }

        private static readonly List<CapturedLabel> s_capturedLabels = new List<CapturedLabel>(64);
        private static readonly Dictionary<int, CityLabelRecord> s_cityLabels = new Dictionary<int, CityLabelRecord>();
        private static bool s_needCacheFlush = false;
        private static readonly HashSet<string> s_nationalCapitals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "London", "Paris", "Madrid", "Lisbon", "Amsterdam", "Stockholm", "Berlin", "Vienna", "Copenhagen", "Petersburg", "Constantinople"
        };
        private static readonly string[] s_seaKeywords = new string[]
        {
            "Ocean", "Sea", "Channel", "Strait", "Straits", "Gulf", "Bay",
            "Sound", "Passage", "Waters", "Coast", "Bank", "Basin", "Reach",
            "Bight", "Firth", "Deep", "Shallows", "Roads", "Narrows"
        };
        private static readonly HashSet<string> s_knownProvinces = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Ireland & Great Britain
            "The Pale", "Ulster", "Munster", "Connaught", "Leinster", "East Anglia", "Kent",
            "Cornwall", "Devon", "Middlesex", "Lancashire", "Yorkshire", "Northumberland",
            "Wales", "Ross", "Ayr", "Highlands", "Inverness", "Stirling", "Lothian",
            // France
            "Normandy", "Brittany", "Gascony", "Picardy", "Provence", "Lorraine", "Loire",
            "Finestere", "Burgundy", "Champagne", "Poitou", "Languedoc", "Auvergne",
            "Dauphine", "Ile-de-France", "Aquitaine", "Flanders",
            // Spain & Portugal
            "Granada", "Aragon", "Andalusia", "Asturias", "Castile", "Catalonia", "Gallicia",
            "Galicia", "Leon", "Navarre", "Valencia", "Extremadura", "Murcia", "Alentejo",
            "Algarve", "Braganza", "Tras Os Montes", "Beira", "Estremadura",
            // Germany, Austria & Central Europe
            "Rhineland", "Brunswick", "Saxony", "Bavaria", "Hanover", "Brandenburg",
            "Silesia", "Bohemia", "Moravia", "Pomerania", "Westphalia", "Hesse",
            "Thuringia", "Franconia", "Swabia", "Pfalz", "Wurttemberg", "Baden",
            "Austria", "Tyrol", "Styria", "Carinthia", "Carniole", "Salzburg", "Hungary", "Transylvania",
            // Low Countries & Scandinavia
            "Friesland", "Groningen", "Overijssel", "Utrecht", "Zeeland", "Holland", "Gelderland",
            "Brabant", "Jutland", "Schleswig", "Holstein", "Sjaelland", "Fyn",
            "Angermanland", "Jamtland", "Smaland", "Uppland", "Gotaland", "Norrland",
            "Vastergotland", "Ostergotland", "Akershus", "Bergenhus", "Trondelag",
            // Switzerland & Italy
            "Aargau", "Glaurus", "Unterwalden", "Uri", "Valais", "Grisons", "Ticino",
            "Savoy", "Piedmont", "Lombardy", "Venetia", "Tuscany", "Romagna", "Umbria",
            "Corsica", "Sardinia", "Sicily", "Calabria", "Apulia", "Campania",
            // Tribal & New World Regions
            "Cherokee", "Iroquois", "Sioux", "Seneca", "Mohawk", "Lenai Lenape",
            "Calusa", "Timucua", "Ciboney", "Arawak", "Carib", "Caribbia",
            "Apache", "Navajo", "Comanche", "Creek", "Seminole", "Shawnee", "Huron", "Inuit",
            // Corrupt engine strings / tokens
            "hoo"
        };
        private static readonly HashSet<string> s_knownCities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // European Capitals & Cities
            "London", "Paris", "Madrid", "Lisbon", "Amsterdam", "Stockholm", "Berlin", "Vienna",
            "Copenhagen", "Petersburg", "Constantinople", "Edinburgh", "Edinburg", "Dublin", "Limerick",
            "Bordeaux", "Marseille", "Lyon", "Nantes", "Rouen", "Trondhjem", "Roskilde", "Hamburg",
            "Bremen", "Kiel", "Munich", "Prague", "Zurich", "Basel", "Bern", "Geneva", "Milan",
            "Genoa", "Florence", "Parma", "Venice", "Rome", "Naples", "Turin", "Palermo", "Warsaw",
            "Cracow", "Budapest", "Belgrade", "Bucharest", "Sofia", "Athens", "Istanbul", "Moscow",
            "Kiev", "Novgorod", "Smolensk", "Riga", "Vilna", "Danzig", "Konigsberg",
            // New World Settlements & Native Capitals
            "Tenochitlan", "Texcoco", "Tlaxcala", "Cholula", "Tlacopan", "Amecameca", "Popocatepetl",
            "Mayapan", "Tipu", "Lamanai", "Ichpaatun", "Cuzco", "Cajamarca", "Jauja", "Huanuco",
            "Limatambo", "Pachacamac", "Machu Picchu", "Ayacucha", "Caracas", "Montreal", "Quebec",
            "Ottawa", "Detroit", "Miami", "Tampa", "Jacksonville", "Boca Raton", "Key Largo",
            "St. Croix", "St. John", "Hobart", "Brisbane", "Ginosar", "Galon", "Eingev", "Amir",
            "Elon", "Yosi", "Osnot", "Harmear", "Abmear", "Guadalupe"
        };
        private static readonly object s_labelLock = new object();
        private static int s_lastSx0 = 0;
        private static int s_lastSy0 = 0;
        private static double s_lastScaleX = 1.0;
        private static double s_lastScaleY = 1.0;

        // AmbitMcWindow Modal Dialog State
        private class McWindowInfo
        {
            public IntPtr Hwnd;
            public int OrigW;
            public int OrigH;
            public int PhysW;
            public int PhysH;
            public IntPtr OrigWndProc;
        }

        private static readonly Dictionary<IntPtr, McWindowInfo> s_mcWindows = new Dictionary<IntPtr, McWindowInfo>();

        // State & Configuration
        private static bool s_initialized;
        private static IntPtr s_gameHwnd = IntPtr.Zero;
        private static IntPtr s_viewHwnd = IntPtr.Zero;
        private static IntPtr s_origViewWndProc = IntPtr.Zero;
        private static IntPtr s_origGameWndProc = IntPtr.Zero;
        private static bool s_presentationConfigured = false;
        private static bool s_gameConfigured = false;
        private static bool s_viewConfigured = false;
        private static DisplayMode s_mode = DisplayMode.Fullscreen;
        private static AspectScale s_scale = AspectScale.Fit;
        private static int s_filter = HALFTONE;
        private static ZoomFilter s_zoomFilter = ZoomFilter.Smooth;
        private static bool s_zoomEnabled = true;
        private static float s_zoomLevel = 1.0f;
        private static int s_zoomFocalX = 8 + 578 / 2;
        private static int s_zoomFocalY = 58 + 512 / 2;
        private static int s_zoomEffectiveSx = 8;
        private static int s_zoomEffectiveSy = 58;
        private static int s_zoomEffectiveSrcW = 578;
        private static int s_zoomEffectiveSrcH = 512;
        private static IntPtr s_cachedCanvasDC = IntPtr.Zero;
        private static IntPtr s_cachedCanvasBmp = IntPtr.Zero;
        private static IntPtr s_scaledFont = IntPtr.Zero;
        private static readonly IntPtr s_pScaledRectBuf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RECT)));
        private static IntPtr s_pActiveMapDialog = IntPtr.Zero;
        private static readonly IntPtr s_pExpandedRectBuf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(VRect)));
        private static IntPtr s_pStubMem = IntPtr.Zero;
        private static IntPtr s_pStubCounter = IntPtr.Zero;
        private static MapDrawStringDelegate s_myMapDrawString = OnMapDrawString;
        private static MapDrawStringDelegate s_origDrawString = null;
        private static bool s_isInsideMapDraw = false;

        // Presentation coordinates
        public static int WindowW { get; private set; }
        public static int WindowH { get; private set; }
        public static int TargetW { get; private set; }
        public static int TargetH { get; private set; }
        public static int DstX { get; private set; }
        public static int DstY { get; private set; }
        public static float CurrentZoom
        {
            get { return s_zoomLevel; }
        }
        public static ZoomFilter CurrentZoomFilter
        {
            get { return s_zoomFilter; }
            set { s_zoomFilter = value; }
        }
        public static int ZoomEffectiveSx { get { return s_zoomEffectiveSx; } }
        public static int ZoomEffectiveSy { get { return s_zoomEffectiveSy; } }
        public static int ZoomEffectiveSrcW { get { return s_zoomEffectiveSrcW; } }
        public static int ZoomEffectiveSrcH { get { return s_zoomEffectiveSrcH; } }
        public static int VTableMapDialogDraw { get { return VTABLE_TMAPDIALOG_DRAW; } }
        public static int VTableMapDialogDrawSelection { get { return VTABLE_TMAPDIALOG_DRAW_SELECTION; } }
        public static int VTableMapDialogScreenToTile { get { return VTABLE_TMAPDIALOG_SCREEN_TO_TILE; } }
        public static int VTableNewMapDialogDrawSelection { get { return VTABLE_TNEWMAPDIALOG_DRAW_SELECTION; } }
        public static int VTableNewMapDialogScreenToTile { get { return VTABLE_TNEWMAPDIALOG_SCREEN_TO_TILE; } }
        public static int LastSx0 { get { return s_lastSx0; } set { s_lastSx0 = value; } }
        public static int LastSy0 { get { return s_lastSy0; } set { s_lastSy0 = value; } }
        public static double LastScaleX { get { return s_lastScaleX; } set { s_lastScaleX = value; } }
        public static double LastScaleY { get { return s_lastScaleY; } set { s_lastScaleY = value; } }
        public static int AddrTMapDialogTileAlloc { get { return ADDR_TMAPDIALOG_TILE_ALLOC; } }
        public static int CallTMapDialogSelectionCopyBits1 { get { return CALL_TMAPDIALOG_SELECTION_COPYBITS1; } }
        public static int CallTMapDialogSelectionCopyBits2 { get { return CALL_TMAPDIALOG_SELECTION_COPYBITS2; } }
        public static int CallTNewMapDialogSelectionCopyBits1 { get { return CALL_TNEWMAPDIALOG_SELECTION_COPYBITS1; } }
        public static int CallTNewMapDialogSelectionCopyBits2 { get { return CALL_TNEWMAPDIALOG_SELECTION_COPYBITS2; } }
        public static int VTableMapDialogDrawSelectionBox { get { return VTABLE_TMAPDIALOG_DRAW_SELECTION_BOX; } }
        public static int VTableNewMapDialogDrawSelectionBox { get { return VTABLE_TNEWMAPDIALOG_DRAW_SELECTION_BOX; } }
        public static int VTableMapDialog3DrawSelectionBox { get { return VTABLE_TMAPDIALOG3_DRAW_SELECTION_BOX; } }
        public static int CallTMapDialogDrawString { get { return CALL_TMAPDIALOG_DRAWSTRING; } }
        public static int AddrDrawStringThunk { get { return ADDR_ORIG_DRAWSTRING; } }
        public static int AddrDrawStringImpl { get { return ADDR_DRAWSTRING_IMPL; } }
        public static List<CapturedLabel> CapturedLabels
        {
            get
            {
                lock (s_labelLock)
                {
                    return new List<CapturedLabel>(s_capturedLabels);
                }
            }
        }
        public static Dictionary<int, CityLabelRecord> CityLabels
        {
            get
            {
                lock (s_labelLock)
                {
                    return new Dictionary<int, CityLabelRecord>(s_cityLabels);
                }
            }
        }
        public static HashSet<string> NationalCapitals
        {
            get { return s_nationalCapitals; }
        }

        public static bool CheckLabelCollision(RECT a, RECT b)
        {
            return (a.Left < b.Right && a.Right > b.Left &&
                    a.Top < b.Bottom && a.Bottom > b.Top);
        }

        public static bool IsOceanName(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 3) return false;
            if (s_knownProvinces.Contains(text)) return false;
            for (int i = 0; i < s_seaKeywords.Length; i++)
            {
                if (text.IndexOf(s_seaKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsCityName(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 3 || text.Length > 24) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 32 || c > 126) return false;
            }
            if (!char.IsUpper(text[0])) return false;
            if (s_knownProvinces.Contains(text)) return false;
            if (IsOceanName(text)) return false;
            if (s_knownCities.Contains(text)) return true;
            return true;
        }

        public static void SetZoom(float zoom, int focalX = 297, int focalY = 314)
        {
            float oldZoom = s_zoomLevel;
            s_zoomLevel = (float)Math.Round(Math.Max(0.25f, Math.Min(3.0f, zoom)), 2);
            if (s_zoomLevel < 1.0f && s_zoomLevel != oldZoom)
            {
                s_needCacheFlush = true;
            }
            if (s_zoomLevel == 1.0f)
            {
                s_zoomFocalX = 8 + 578 / 2;
                s_zoomFocalY = 58 + 512 / 2;
                s_zoomEffectiveSx = 8;
                s_zoomEffectiveSy = 58;
                s_zoomEffectiveSrcW = 578;
                s_zoomEffectiveSrcH = 512;
            }
            else if (s_zoomLevel > 1.0f)
            {
                s_zoomFocalX = focalX;
                s_zoomFocalY = focalY;
                int zoomSrcW = (int)Math.Round(578.0 / s_zoomLevel);
                int zoomSrcH = (int)Math.Round(512.0 / s_zoomLevel);
                int sx = s_zoomFocalX - zoomSrcW / 2;
                int sy = s_zoomFocalY - zoomSrcH / 2;
                if (sx < 8) sx = 8;
                if (sx + zoomSrcW > 586) sx = 586 - zoomSrcW;
                if (sy < 58) sy = 58;
                if (sy + zoomSrcH > 570) sy = 570 - zoomSrcH;
                s_zoomEffectiveSx = sx;
                s_zoomEffectiveSy = sy;
                s_zoomEffectiveSrcW = zoomSrcW;
                s_zoomEffectiveSrcH = zoomSrcH;
            }
            else // s_zoomLevel < 1.0f (Option B tactical camera zoom-out)
            {
                int zoomSrcW = (int)Math.Ceiling(576.0 / (double)s_zoomLevel);
                int zoomSrcH = (int)Math.Ceiling(512.0 / (double)s_zoomLevel);
                s_zoomFocalX = 10 + zoomSrcW / 2;
                s_zoomFocalY = 58 + zoomSrcH / 2;
                s_zoomEffectiveSx = 10;
                s_zoomEffectiveSy = 58;
                s_zoomEffectiveSrcW = zoomSrcW;
                s_zoomEffectiveSrcH = zoomSrcH;
            }
        }

        public static void MapZoomCoordinate(int gameX, int gameY, out int mappedX, out int mappedY)
        {
            if (!s_zoomEnabled || s_zoomLevel == 1.0f || gameX < 8 || gameX > 586 || gameY < 58 || gameY > 570)
            {
                mappedX = gameX;
                mappedY = gameY;
                return;
            }

            if (s_zoomLevel > 1.0f)
            {
                double u = (gameX - 8) / 578.0;
                double v = (gameY - 58) / 512.0;
                mappedX = (int)Math.Round(s_zoomEffectiveSx + u * s_zoomEffectiveSrcW);
                mappedY = (int)Math.Round(s_zoomEffectiveSy + v * s_zoomEffectiveSrcH);

                if (mappedX < 8) mappedX = 8;
                if (mappedX > 586) mappedX = 586;
                if (mappedY < 58) mappedY = 58;
                if (mappedY > 570) mappedY = 570;
            }
            else // s_zoomLevel < 1.0f (Option B tactical camera zoom-out)
            {
                double relX = gameX - 10;
                double relY = gameY - 58;
                mappedX = 10 + (int)Math.Round(relX / (double)s_zoomLevel);
                mappedY = 58 + (int)Math.Round(relY / (double)s_zoomLevel);

                if (mappedX < 10) mappedX = 10;
                if (mappedX > 2390) mappedX = 2390;
                if (mappedY < 58) mappedY = 58;
                if (mappedY > 2230) mappedY = 2230;
            }
        }

        // Pinned hook delegates to prevent garbage collection
        private static readonly BitBltDelegate s_myBitBlt = MyBitBltHook;
        private static readonly StretchDIBitsDelegate s_myStretchDIBits = MyStretchDIBitsHook;
        private static readonly MoveWindowDelegate s_myMoveWindow = MyMoveWindowHook;
        private static readonly ClientToScreenDelegate s_myClientToScreen = MyClientToScreenHook;
        private static readonly CreateWindowExDelegate s_myCreateWindowEx = MyCreateWindowExHook;
        private static readonly ScreenToClientDelegate s_myScreenToClient = MyScreenToClientHook;
        private static readonly GetClientRectDelegate s_myGetClientRect = MyGetClientRectHook;
        private static readonly SetWindowPosDelegate s_mySetWindowPos = MySetWindowPosHook;
        private static readonly DeferWindowPosDelegate s_myDeferWindowPos = MyDeferWindowPosHook;
        private static readonly InvalidateRectDelegate s_myInvalidateRect = MyInvalidateRectHook;
        private static readonly ValidateRectDelegate s_myValidateRect = MyValidateRectHook;
        private static readonly BeginPaintDelegate s_myBeginPaint = MyBeginPaintHook;
        private static readonly GetClipBoxDelegate s_myGetClipBox = MyGetClipBoxHook;
        private static readonly IntersectClipRectDelegate s_myIntersectClipRect = MyIntersectClipRectHook;
        private static readonly ExcludeClipRectDelegate s_myExcludeClipRect = MyExcludeClipRectHook;
        private static readonly MoveToExDelegate s_myMoveToEx = MyMoveToExHook;
        private static readonly LineToDelegate s_myLineTo = MyLineToHook;
        private static readonly PolylineDelegate s_myPolyline = MyPolylineHook;
        private static readonly RectangleDelegate s_myRectangle = MyRectangleHook;
        private static readonly SelectClipRgnDelegate s_mySelectClipRgn = MySelectClipRgnHook;
        private static readonly ExtSelectClipRgnDelegate s_myExtSelectClipRgn = MyExtSelectClipRgnHook;
        private static readonly ScrollWindowDelegate s_myScrollWindow = MyScrollWindowHook;
        private static readonly ScrollWindowExDelegate s_myScrollWindowEx = MyScrollWindowExHook;
        private static readonly FillRectDelegate s_myFillRect = MyFillRectHook;
        private static readonly FrameRectDelegate s_myFrameRect = MyFrameRectHook;
        private static readonly WndProcDelegate s_myViewWndProc = ViewWndProc;
        private static readonly WndProcDelegate s_myGameWndProc = GameWndProc;
        private static readonly WndProcDelegate s_myMcWndProc = McWndProc;

        // Option B Tactical Camera Engine Delegates
        private static readonly MapDialogDrawDelegate s_myMapDialogDraw = MyMapDialogDrawHook;
        private static readonly MapDrawSelectionDelegate s_myMapDrawSelection = MyMapDrawSelectionHook;
        private static readonly MapDrawSelectionBoxDelegate s_myMapDrawSelectionBox = MyMapDrawSelectionBoxHook;
        private static readonly MapScreenToTileDelegate s_myMapScreenToTile = MyMapScreenToTileHook;
        private static readonly MapCopyBitsDelegate s_myMapCopyBits = MyMapCopyBitsHook;
        private static readonly MapCopyBitsDelegate s_myMapSelectionCopyBits = MyMapSelectionCopyBitsHook;

        // Original function pointers
        private static BitBltDelegate s_origBitBlt;
        private static StretchDIBitsDelegate s_origStretchDIBits;
        private static MoveWindowDelegate s_origMoveWindow;
        private static ClientToScreenDelegate s_origClientToScreen;
        private static CreateWindowExDelegate s_origCreateWindowEx;
        private static ScreenToClientDelegate s_origScreenToClient;
        private static GetClientRectDelegate s_origGetClientRect;
        private static SetWindowPosDelegate s_origSetWindowPos;
        private static DeferWindowPosDelegate s_origDeferWindowPos;
        private static InvalidateRectDelegate s_origInvalidateRect;
        private static ValidateRectDelegate s_origValidateRect;
        private static BeginPaintDelegate s_origBeginPaint;
        private static GetClipBoxDelegate s_origGetClipBox;
        private static IntersectClipRectDelegate s_origIntersectClipRect;
        private static ExcludeClipRectDelegate s_origExcludeClipRect;
        private static MoveToExDelegate s_origMoveToEx;
        private static LineToDelegate s_origLineTo;
        private static PolylineDelegate s_origPolyline;
        private static RectangleDelegate s_origRectangle;
        private static SelectClipRgnDelegate s_origSelectClipRgn;
        private static ExtSelectClipRgnDelegate s_origExtSelectClipRgn;
        private static ScrollWindowDelegate s_origScrollWindow;
        private static ScrollWindowExDelegate s_origScrollWindowEx;
        private static FillRectDelegate s_origFillRect;
        private static FrameRectDelegate s_origFrameRect;

        // Option B Original Function Pointers
        private static MapDialogDrawDelegate s_origMapDialogDraw;
        private static MapDrawSelectionDelegate s_origMapDrawSelection;
        private static MapDrawSelectionBoxDelegate s_origMapDrawSelectionBox;
        private static MapScreenToTileDelegate s_origMapScreenToTile;
        private static MapCopyBitsDelegate s_origCopyBits;

        private static void Log(string fmt, params object[] args)
        {
            try
            {
                string msg = string.Format("[{0:HH:mm:ss.fff}] [GraphicsBridge] ", DateTime.Now) + string.Format(fmt, args) + Environment.NewLine;
                File.AppendAllText("graphics.log", msg, Encoding.UTF8);
            }
            catch { }
        }

        public static int Initialize(IntPtr pvReserved)
        {
            int hr = CoInitialize(pvReserved);
            if (hr == 1 || hr == unchecked((int)0x80010106)) hr = 0;

            if (s_initialized) return hr;
            s_initialized = true;

            try
            {
                Log("=== Imperialism II High-Resolution Presentation Bridge Starting ===");
                ReadConfiguration();

                IntPtr hExe = GetModuleHandleA(null);
                if (hExe == IntPtr.Zero)
                {
                    Log("Warning: Could not get main module handle.");
                    return hr;
                }

                StringBuilder sb = new StringBuilder(260);
                GetModuleFileNameA(hExe, sb, 260);
                string exeName = Path.GetFileName(sb.ToString());
                bool isImp2 = exeName.IndexOf("Imperialism", StringComparison.OrdinalIgnoreCase) >= 0 || exeName.IndexOf("ImpII", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isImp2)
                {
                    Log("Host process '{0}' is not Imperialism II (test runner detected). Skipping IAT hooks.", exeName);
                    return hr;
                }

                // Unprotect IAT range if necessary and install hooks (covers GDI32 and USER32 IAT entries)
                uint oldProtect;
                VirtualProtect((IntPtr)0x0076BD00, (UIntPtr)0x1000, PAGE_EXECUTE_READWRITE, out oldProtect);

                // 1. BitBlt
                IntPtr origBitBltPtr = Marshal.ReadIntPtr((IntPtr)IAT_BITBLT);
                s_origBitBlt = (BitBltDelegate)Marshal.GetDelegateForFunctionPointer(origBitBltPtr, typeof(BitBltDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_BITBLT, Marshal.GetFunctionPointerForDelegate(s_myBitBlt));

                // 2. StretchDIBits
                IntPtr origStretchPtr = Marshal.ReadIntPtr((IntPtr)IAT_STRETCHDIBITS);
                s_origStretchDIBits = (StretchDIBitsDelegate)Marshal.GetDelegateForFunctionPointer(origStretchPtr, typeof(StretchDIBitsDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_STRETCHDIBITS, Marshal.GetFunctionPointerForDelegate(s_myStretchDIBits));

                // 3. MoveWindow
                IntPtr origMovePtr = Marshal.ReadIntPtr((IntPtr)IAT_MOVEWINDOW);
                s_origMoveWindow = (MoveWindowDelegate)Marshal.GetDelegateForFunctionPointer(origMovePtr, typeof(MoveWindowDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_MOVEWINDOW, Marshal.GetFunctionPointerForDelegate(s_myMoveWindow));

                // 4. ClientToScreen
                IntPtr origClientToScreenPtr = Marshal.ReadIntPtr((IntPtr)IAT_CLIENTTOSCREEN);
                s_origClientToScreen = (ClientToScreenDelegate)Marshal.GetDelegateForFunctionPointer(origClientToScreenPtr, typeof(ClientToScreenDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_CLIENTTOSCREEN, Marshal.GetFunctionPointerForDelegate(s_myClientToScreen));

                // 5. CreateWindowExA
                IntPtr origCreatePtr = Marshal.ReadIntPtr((IntPtr)IAT_CREATEWINDOWEXA);
                s_origCreateWindowEx = (CreateWindowExDelegate)Marshal.GetDelegateForFunctionPointer(origCreatePtr, typeof(CreateWindowExDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_CREATEWINDOWEXA, Marshal.GetFunctionPointerForDelegate(s_myCreateWindowEx));

                // 6. ScreenToClient
                IntPtr origScreenPtr = Marshal.ReadIntPtr((IntPtr)IAT_SCREENTOCLIENT);
                s_origScreenToClient = (ScreenToClientDelegate)Marshal.GetDelegateForFunctionPointer(origScreenPtr, typeof(ScreenToClientDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_SCREENTOCLIENT, Marshal.GetFunctionPointerForDelegate(s_myScreenToClient));

                // 7. GetClientRect
                IntPtr origGetClientPtr = Marshal.ReadIntPtr((IntPtr)IAT_GETCLIENTRECT);
                s_origGetClientRect = (GetClientRectDelegate)Marshal.GetDelegateForFunctionPointer(origGetClientPtr, typeof(GetClientRectDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_GETCLIENTRECT, Marshal.GetFunctionPointerForDelegate(s_myGetClientRect));

                // 8. SetWindowPos
                IntPtr origSetPosPtr = Marshal.ReadIntPtr((IntPtr)IAT_SETWINDOWPOS);
                s_origSetWindowPos = (SetWindowPosDelegate)Marshal.GetDelegateForFunctionPointer(origSetPosPtr, typeof(SetWindowPosDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_SETWINDOWPOS, Marshal.GetFunctionPointerForDelegate(s_mySetWindowPos));

                // 9. DeferWindowPos
                IntPtr origDeferPtr = Marshal.ReadIntPtr((IntPtr)IAT_DEFERWINDOWPOS);
                s_origDeferWindowPos = (DeferWindowPosDelegate)Marshal.GetDelegateForFunctionPointer(origDeferPtr, typeof(DeferWindowPosDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_DEFERWINDOWPOS, Marshal.GetFunctionPointerForDelegate(s_myDeferWindowPos));

                // 10. InvalidateRect
                IntPtr origInvalidatePtr = Marshal.ReadIntPtr((IntPtr)IAT_INVALIDATERECT);
                s_origInvalidateRect = (InvalidateRectDelegate)Marshal.GetDelegateForFunctionPointer(origInvalidatePtr, typeof(InvalidateRectDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_INVALIDATERECT, Marshal.GetFunctionPointerForDelegate(s_myInvalidateRect));

                // 11. ValidateRect
                IntPtr origValidatePtr = Marshal.ReadIntPtr((IntPtr)IAT_VALIDATERECT);
                s_origValidateRect = (ValidateRectDelegate)Marshal.GetDelegateForFunctionPointer(origValidatePtr, typeof(ValidateRectDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_VALIDATERECT, Marshal.GetFunctionPointerForDelegate(s_myValidateRect));

                // 12. BeginPaint
                IntPtr origBeginPaintPtr = Marshal.ReadIntPtr((IntPtr)IAT_BEGINPAINT);
                s_origBeginPaint = (BeginPaintDelegate)Marshal.GetDelegateForFunctionPointer(origBeginPaintPtr, typeof(BeginPaintDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_BEGINPAINT, Marshal.GetFunctionPointerForDelegate(s_myBeginPaint));

                // 13. GetClipBox
                IntPtr origGetClipBoxPtr = Marshal.ReadIntPtr((IntPtr)IAT_GETCLIPBOX);
                s_origGetClipBox = (GetClipBoxDelegate)Marshal.GetDelegateForFunctionPointer(origGetClipBoxPtr, typeof(GetClipBoxDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_GETCLIPBOX, Marshal.GetFunctionPointerForDelegate(s_myGetClipBox));

                // 14. IntersectClipRect
                IntPtr origIntersectPtr = Marshal.ReadIntPtr((IntPtr)IAT_INTERSECTCLIPRECT);
                s_origIntersectClipRect = (IntersectClipRectDelegate)Marshal.GetDelegateForFunctionPointer(origIntersectPtr, typeof(IntersectClipRectDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_INTERSECTCLIPRECT, Marshal.GetFunctionPointerForDelegate(s_myIntersectClipRect));

                // 15. ExcludeClipRect
                IntPtr origExcludePtr = Marshal.ReadIntPtr((IntPtr)IAT_EXCLUDECLIPRECT);
                s_origExcludeClipRect = (ExcludeClipRectDelegate)Marshal.GetDelegateForFunctionPointer(origExcludePtr, typeof(ExcludeClipRectDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_EXCLUDECLIPRECT, Marshal.GetFunctionPointerForDelegate(s_myExcludeClipRect));

                // 16. Polyline
                IntPtr origPolyPtr = Marshal.ReadIntPtr((IntPtr)IAT_POLYLINE);
                s_origPolyline = (PolylineDelegate)Marshal.GetDelegateForFunctionPointer(origPolyPtr, typeof(PolylineDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_POLYLINE, Marshal.GetFunctionPointerForDelegate(s_myPolyline));

                // 17. Rectangle
                IntPtr origRectPtr = Marshal.ReadIntPtr((IntPtr)IAT_RECTANGLE);
                s_origRectangle = (RectangleDelegate)Marshal.GetDelegateForFunctionPointer(origRectPtr, typeof(RectangleDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_RECTANGLE, Marshal.GetFunctionPointerForDelegate(s_myRectangle));

                // 18. SelectClipRgn
                IntPtr origSelClipPtr = Marshal.ReadIntPtr((IntPtr)IAT_SELECTCLIPRGN);
                s_origSelectClipRgn = (SelectClipRgnDelegate)Marshal.GetDelegateForFunctionPointer(origSelClipPtr, typeof(SelectClipRgnDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_SELECTCLIPRGN, Marshal.GetFunctionPointerForDelegate(s_mySelectClipRgn));

                // 19. ExtSelectClipRgn
                IntPtr origExtSelClipPtr = Marshal.ReadIntPtr((IntPtr)IAT_EXTSELECTCLIPRGN);
                s_origExtSelectClipRgn = (ExtSelectClipRgnDelegate)Marshal.GetDelegateForFunctionPointer(origExtSelClipPtr, typeof(ExtSelectClipRgnDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_EXTSELECTCLIPRGN, Marshal.GetFunctionPointerForDelegate(s_myExtSelectClipRgn));

                // 20. MoveToEx
                IntPtr origMoveToPtr = Marshal.ReadIntPtr((IntPtr)IAT_MOVETOEX);
                s_origMoveToEx = (MoveToExDelegate)Marshal.GetDelegateForFunctionPointer(origMoveToPtr, typeof(MoveToExDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_MOVETOEX, Marshal.GetFunctionPointerForDelegate(s_myMoveToEx));

                // 21. LineTo
                IntPtr origLineToPtr = Marshal.ReadIntPtr((IntPtr)IAT_LINETO);
                s_origLineTo = (LineToDelegate)Marshal.GetDelegateForFunctionPointer(origLineToPtr, typeof(LineToDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_LINETO, Marshal.GetFunctionPointerForDelegate(s_myLineTo));

                // 22. ScrollWindow
                IntPtr origScrollPtr = Marshal.ReadIntPtr((IntPtr)IAT_SCROLLWINDOW);
                s_origScrollWindow = (ScrollWindowDelegate)Marshal.GetDelegateForFunctionPointer(origScrollPtr, typeof(ScrollWindowDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_SCROLLWINDOW, Marshal.GetFunctionPointerForDelegate(s_myScrollWindow));

                // 23. ScrollWindowEx
                IntPtr origScrollExPtr = Marshal.ReadIntPtr((IntPtr)IAT_SCROLLWINDOWEX);
                s_origScrollWindowEx = (ScrollWindowExDelegate)Marshal.GetDelegateForFunctionPointer(origScrollExPtr, typeof(ScrollWindowExDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_SCROLLWINDOWEX, Marshal.GetFunctionPointerForDelegate(s_myScrollWindowEx));

                // 24. FillRect
                IntPtr origFillPtr = Marshal.ReadIntPtr((IntPtr)IAT_FILLRECT);
                s_origFillRect = (FillRectDelegate)Marshal.GetDelegateForFunctionPointer(origFillPtr, typeof(FillRectDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_FILLRECT, Marshal.GetFunctionPointerForDelegate(s_myFillRect));

                // 25. FrameRect
                IntPtr origFramePtr = Marshal.ReadIntPtr((IntPtr)IAT_FRAMERECT);
                s_origFrameRect = (FrameRectDelegate)Marshal.GetDelegateForFunctionPointer(origFramePtr, typeof(FrameRectDelegate));
                Marshal.WriteIntPtr((IntPtr)IAT_FRAMERECT, Marshal.GetFunctionPointerForDelegate(s_myFrameRect));

                // 26. Option B Tactical Camera Engine Hooks & Buffer Expansion
                InstallOptionBEngineHooks();

                Log("IAT hooks successfully installed (BitBlt, StretchDIBits, MoveWindow, ClientToScreen, CreateWindowExA, ScreenToClient, GetClientRect, SetWindowPos, DeferWindowPos, InvalidateRect, ValidateRect, BeginPaint, GetClipBox, IntersectClipRect, ExcludeClipRect, Polyline, Rectangle, SelectClipRgn, ExtSelectClipRgn, MoveToEx, LineTo, ScrollWindow, ScrollWindowEx, FillRect, FrameRect). Mode={0}, Scale={1}, Filter={2}",
                    s_mode, s_scale, s_filter == HALFTONE ? "Halftone" : "Nearest");
            }
            catch (Exception ex)
            {
                Log("Fatal initialization error: {0}", ex);
            }

            return hr;
        }

        private static void ReadConfiguration()
        {
            try
            {
                string iniPath = "dplayx.ini";
                if (!File.Exists(iniPath)) return;

                string[] lines = File.ReadAllLines(iniPath);
                bool inGraphics = false;
                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inGraphics = line.Equals("[Graphics]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inGraphics) continue;

                    string[] parts = line.Split(new char[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    string key = parts[0].Trim();
                    string val = parts[1].Trim();

                    if (key.Equals("Mode", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val.Equals("Windowed", StringComparison.OrdinalIgnoreCase))
                            s_mode = DisplayMode.Windowed;
                        else
                            s_mode = DisplayMode.Fullscreen;
                    }
                    else if (key.Equals("Scale", StringComparison.OrdinalIgnoreCase) || key.Equals("AspectRatio", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val.Equals("Stretch", StringComparison.OrdinalIgnoreCase))
                            s_scale = AspectScale.Stretch;
                        else
                            s_scale = AspectScale.Fit;
                    }
                    else if (key.Equals("Filter", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val.Equals("Nearest", StringComparison.OrdinalIgnoreCase))
                            s_filter = COLORONCOLOR;
                        else
                            s_filter = HALFTONE;
                    }
                    else if (key.Equals("ZoomFilter", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val.Equals("Smooth", StringComparison.OrdinalIgnoreCase) || val.Equals("Box", StringComparison.OrdinalIgnoreCase))
                            s_zoomFilter = ZoomFilter.Smooth;
                        else if (val.Equals("Sharp", StringComparison.OrdinalIgnoreCase))
                            s_zoomFilter = ZoomFilter.Sharp;
                        else
                            s_zoomFilter = ZoomFilter.Nearest;
                    }
                    else if (key.Equals("Zoom", StringComparison.OrdinalIgnoreCase) || key.Equals("ZoomEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        s_zoomEnabled = (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Error reading configuration: {0}", ex.Message);
            }
        }

        public static void RecalculateGeometry(int clientW, int clientH)
        {
            WindowW = clientW;
            WindowH = clientH;

            if (s_scale == AspectScale.Stretch)
            {
                TargetW = clientW;
                TargetH = clientH;
                DstX = 0;
                DstY = 0;
            }
            else
            {
                // Maintain 4:3 aspect ratio
                if (clientW * 3 > clientH * 4)
                {
                    // Widescreen monitor (16:9, 16:10, 21:9)
                    TargetH = clientH;
                    TargetW = clientH * 4 / 3;
                    DstX = (clientW - TargetW) / 2;
                    DstY = 0;
                }
                else
                {
                    // Narrow / portrait
                    TargetW = clientW;
                    TargetH = clientW * 3 / 4;
                    DstX = 0;
                    DstY = (clientH - TargetH) / 2;
                }
            }
        }

        private static void ApplyScaledFont(IntPtr hWndChild)
        {
            try
            {
                if (s_scaledFont == IntPtr.Zero && TargetH > 0)
                {
                    int fontHeight = Math.Max(14, (int)Math.Round(14.0 * TargetH / 600.0));
                    s_scaledFont = CreateFontA(
                        fontHeight, 0, 0, 0, 700 /* FW_BOLD */,
                        0, 0, 0, 0, 0, 0, 0, 0, "Arial");
                    Log("Created scaled UI font: height={0}px", fontHeight);
                }

                if (s_scaledFont != IntPtr.Zero)
                {
                    SendMessageA(hWndChild, WM_SETFONT, s_scaledFont, (IntPtr)1);
                }
            }
            catch (Exception ex)
            {
                Log("ApplyScaledFont error: {0}", ex.Message);
            }
        }

        private static IntPtr MyCreateWindowExHook(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam)
        {
            bool isGameView = lpClassName != null && lpClassName.IndexOf("AmbitGameWindow", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isMcWindow = lpClassName != null && lpClassName.IndexOf("AmbitMcWindow", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isChildOfView = (hWndParent == s_viewHwnd || hWndParent == s_gameHwnd) && !isGameView && !isMcWindow;

            int origX = x, origY = y, origW = nWidth, origH = nHeight;

            if (isMcWindow)
            {
                dwStyle |= (WS_VISIBLE | WS_CLIPSIBLINGS);
                if (TargetW > 0 && TargetH > 0 && x != unchecked((int)0x80000000))
                {
                    x = DstX + (int)Math.Round((double)x * TargetW / 800.0);
                    y = DstY + (int)Math.Round((double)y * TargetH / 600.0);
                    nWidth = (int)Math.Round((double)nWidth * TargetW / 800.0);
                    nHeight = (int)Math.Round((double)nHeight * TargetH / 600.0);
                }
            }
            else if (isGameView)
            {
                dwStyle |= (WS_CLIPSIBLINGS | WS_CLIPCHILDREN);
            }
            else if (isChildOfView && TargetW > 0 && TargetH > 0)
            {
                if (x != unchecked((int)0x80000000))
                {
                    McWindowInfo mcParent;
                    if (s_mcWindows.TryGetValue(hWndParent, out mcParent))
                    {
                        x = (int)Math.Round((double)x * mcParent.PhysW / mcParent.OrigW);
                        y = (int)Math.Round((double)y * mcParent.PhysH / mcParent.OrigH);
                        nWidth = (int)Math.Round((double)nWidth * mcParent.PhysW / mcParent.OrigW);
                        nHeight = (int)Math.Round((double)nHeight * mcParent.PhysH / mcParent.OrigH);
                    }
                    else
                    {
                        x = DstX + (int)Math.Round((double)x * TargetW / 800.0);
                        y = DstY + (int)Math.Round((double)y * TargetH / 600.0);
                        nWidth = (int)Math.Round((double)nWidth * TargetW / 800.0);
                        nHeight = (int)Math.Round((double)nHeight * TargetH / 600.0);
                    }
                }
            }

            IntPtr hWnd = s_origCreateWindowEx(dwExStyle, lpClassName, lpWindowName, dwStyle, x, y, nWidth, nHeight, hWndParent, hMenu, hInstance, lpParam);
            Log("CreateWindowEx: class='{0}', title='{1}', parent=0x{2:X}, style=0x{3:X8}, orig=({4},{5},{6}x{7}), pos=({8},{9},{10}x{11}) -> hWnd=0x{12:X}",
                lpClassName, lpWindowName, hWndParent.ToInt32(), dwStyle, origX, origY, origW, origH, x, y, nWidth, nHeight, hWnd.ToInt32());

            if (isMcWindow && hWnd != IntPtr.Zero)
            {
                McWindowInfo mc = new McWindowInfo
                {
                    Hwnd = hWnd,
                    OrigW = (origW > 0 ? origW : 1),
                    OrigH = (origH > 0 ? origH : 1),
                    PhysW = (nWidth > 0 ? nWidth : 1),
                    PhysH = (nHeight > 0 ? nHeight : 1),
                    OrigWndProc = IntPtr.Zero
                };
                s_mcWindows[hWnd] = mc;

                IntPtr mcProcPtr = Marshal.GetFunctionPointerForDelegate(s_myMcWndProc);
                mc.OrigWndProc = SetWindowLongA(hWnd, GWL_WNDPROC, mcProcPtr);
                Log("Tracked and subclassed AmbitMcWindow 0x{0:X}: orig=({1},{2},{3}x{4}) -> phys=({5},{6},{7}x{8}), origWndProc=0x{9:X}",
                    hWnd.ToInt32(), origX, origY, origW, origH, x, y, nWidth, nHeight, mc.OrigWndProc.ToInt32());

                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                BringWindowToTop(hWnd);
                ShowWindow(hWnd, 5); // SW_SHOW
            }

            if (isGameView && hWnd != IntPtr.Zero)
            {
                s_viewHwnd = hWnd;
                Log("Identified game view window (AmbitGameWindow): hWnd=0x{0:X}", hWnd.ToInt32());
                if (hWndParent != IntPtr.Zero && s_gameHwnd == IntPtr.Zero)
                {
                    s_gameHwnd = hWndParent;
                    Log("Identified game main window from view parent: hWnd=0x{0:X}", s_gameHwnd.ToInt32());
                }
                EnsurePresentationConfigured();
            }

            bool isMainWindow = (hWndParent == IntPtr.Zero) &&
                (!string.IsNullOrEmpty(lpWindowName) && lpWindowName.IndexOf("Imperialism", StringComparison.OrdinalIgnoreCase) >= 0);

            if (isMainWindow && hWnd != IntPtr.Zero)
            {
                s_gameHwnd = hWnd;
                Log("Identified game main window: hWnd=0x{0:X}", hWnd.ToInt32());
                EnsurePresentationConfigured();
            }

            if (isChildOfView && hWnd != IntPtr.Zero && TargetH > 0)
            {
                ApplyScaledFont(hWnd);
            }

            return hWnd;
        }

        private static bool MyMoveWindowHook(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint)
        {
            Log("MoveWindow: hWnd=0x{0:X}, pos=({1},{2}), size=({3}x{4}), repaint={5} (view=0x{6:X}, game=0x{7:X})",
                hWnd.ToInt32(), X, Y, nWidth, nHeight, bRepaint, s_viewHwnd.ToInt32(), s_gameHwnd.ToInt32());

            McWindowInfo mc;
            if (s_mcWindows.TryGetValue(hWnd, out mc) && TargetW > 0 && TargetH > 0)
            {
                int sx = DstX + (int)Math.Round((double)X * TargetW / 800.0);
                int sy = DstY + (int)Math.Round((double)Y * TargetH / 600.0);
                int sw = (int)Math.Round((double)nWidth * TargetW / 800.0);
                int sh = (int)Math.Round((double)nHeight * TargetH / 600.0);
                mc.PhysW = (sw > 0 ? sw : 1);
                mc.PhysH = (sh > 0 ? sh : 1);
                return s_origMoveWindow(hWnd, sx, sy, sw, sh, bRepaint);
            }

            if (hWnd == s_viewHwnd)
            {
                EnsurePresentationConfigured();
                if (WindowW > 0 && WindowH > 0)
                {
                    return s_origMoveWindow(hWnd, 0, 0, WindowW, WindowH, bRepaint);
                }
                return s_origMoveWindow(hWnd, X, Y, nWidth, nHeight, bRepaint);
            }

            if (hWnd == s_gameHwnd)
            {
                EnsurePresentationConfigured();
                if (s_mode == DisplayMode.Fullscreen)
                {
                    int screenW = Screen.PrimaryScreen.Bounds.Width;
                    int screenH = Screen.PrimaryScreen.Bounds.Height;
                    return s_origMoveWindow(hWnd, 0, 0, screenW, screenH, bRepaint);
                }
                else if (WindowW > 0 && WindowH > 0)
                {
                    return s_origMoveWindow(hWnd, X, Y, WindowW, WindowH, bRepaint);
                }
            }

            IntPtr parent = GetParent(hWnd);
            if (parent != IntPtr.Zero && TargetW > 0 && TargetH > 0)
            {
                McWindowInfo mcParent;
                if (s_mcWindows.TryGetValue(parent, out mcParent))
                {
                    int sx = (int)Math.Round((double)X * mcParent.PhysW / mcParent.OrigW);
                    int sy = (int)Math.Round((double)Y * mcParent.PhysH / mcParent.OrigH);
                    int sw = (int)Math.Round((double)nWidth * mcParent.PhysW / mcParent.OrigW);
                    int sh = (int)Math.Round((double)nHeight * mcParent.PhysH / mcParent.OrigH);
                    return s_origMoveWindow(hWnd, sx, sy, sw, sh, bRepaint);
                }
                else if (parent == s_viewHwnd)
                {
                    int sx = DstX + (int)Math.Round((double)X * TargetW / 800.0);
                    int sy = DstY + (int)Math.Round((double)Y * TargetH / 600.0);
                    int sw = (int)Math.Round((double)nWidth * TargetW / 800.0);
                    int sh = (int)Math.Round((double)nHeight * TargetH / 600.0);
                    return s_origMoveWindow(hWnd, sx, sy, sw, sh, bRepaint);
                }
            }

            return s_origMoveWindow(hWnd, X, Y, nWidth, nHeight, bRepaint);
        }

        private static bool MySetWindowPosHook(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags)
        {
            Log("SetWindowPos: hWnd=0x{0:X}, after=0x{1:X}, pos=({2},{3}), size=({4}x{5}), flags=0x{6:X4} (view=0x{7:X}, game=0x{8:X})",
                hWnd.ToInt32(), hWndInsertAfter.ToInt32(), X, Y, cx, cy, uFlags, s_viewHwnd.ToInt32(), s_gameHwnd.ToInt32());

            McWindowInfo mc;
            if (s_mcWindows.TryGetValue(hWnd, out mc) && TargetW > 0 && TargetH > 0)
            {
                if ((uFlags & SWP_NOMOVE) == 0)
                {
                    X = DstX + (int)Math.Round((double)X * TargetW / 800.0);
                    Y = DstY + (int)Math.Round((double)Y * TargetH / 600.0);
                }
                if ((uFlags & SWP_NOSIZE) == 0)
                {
                    cx = (int)Math.Round((double)cx * TargetW / 800.0);
                    cy = (int)Math.Round((double)cy * TargetH / 600.0);
                    mc.PhysW = (cx > 0 ? cx : 1);
                    mc.PhysH = (cy > 0 ? cy : 1);
                }
                if (hWndInsertAfter == (IntPtr)unchecked((int)0xFFFFFFFF))
                {
                    hWndInsertAfter = IntPtr.Zero; // HWND_TOP for child windows
                }
                uFlags |= SWP_SHOWWINDOW;
                uFlags &= ~SWP_NOZORDER;
                return s_origSetWindowPos(hWnd, hWndInsertAfter, X, Y, cx, cy, uFlags);
            }

            if (hWnd == s_viewHwnd)
            {
                EnsurePresentationConfigured();
                if (WindowW > 0 && WindowH > 0)
                {
                    X = 0;
                    Y = 0;
                    cx = WindowW;
                    cy = WindowH;
                    uFlags &= ~SWP_NOMOVE;
                    uFlags &= ~SWP_NOSIZE;
                }
                uFlags &= ~SWP_NOACTIVATE;
                return s_origSetWindowPos(hWnd, hWndInsertAfter, X, Y, cx, cy, uFlags);
            }

            if (hWnd == s_gameHwnd)
            {
                EnsurePresentationConfigured();
                if (s_mode == DisplayMode.Fullscreen)
                {
                    int screenW = Screen.PrimaryScreen.Bounds.Width;
                    int screenH = Screen.PrimaryScreen.Bounds.Height;
                    X = 0;
                    Y = 0;
                    cx = screenW;
                    cy = screenH;
                    uFlags &= ~SWP_NOMOVE;
                    uFlags &= ~SWP_NOSIZE;
                    if (hWndInsertAfter == (IntPtr)1)
                    {
                        hWndInsertAfter = IntPtr.Zero;
                    }
                    uFlags &= ~SWP_NOACTIVATE;
                    return s_origSetWindowPos(hWnd, hWndInsertAfter, X, Y, cx, cy, uFlags);
                }
                else if (WindowW > 0 && WindowH > 0)
                {
                    if ((uFlags & SWP_NOSIZE) == 0)
                    {
                        cx = WindowW;
                        cy = WindowH;
                    }
                    if (hWndInsertAfter == (IntPtr)1)
                    {
                        hWndInsertAfter = IntPtr.Zero;
                    }
                    uFlags &= ~SWP_NOACTIVATE;
                    return s_origSetWindowPos(hWnd, hWndInsertAfter, X, Y, cx, cy, uFlags);
                }
            }

            IntPtr parent = GetParent(hWnd);
            if (parent != IntPtr.Zero && TargetW > 0 && TargetH > 0)
            {
                McWindowInfo mcParent;
                if (s_mcWindows.TryGetValue(parent, out mcParent))
                {
                    if ((uFlags & SWP_NOMOVE) == 0)
                    {
                        X = (int)Math.Round((double)X * mcParent.PhysW / mcParent.OrigW);
                        Y = (int)Math.Round((double)Y * mcParent.PhysH / mcParent.OrigH);
                    }
                    if ((uFlags & SWP_NOSIZE) == 0)
                    {
                        cx = (int)Math.Round((double)cx * mcParent.PhysW / mcParent.OrigW);
                        cy = (int)Math.Round((double)cy * mcParent.PhysH / mcParent.OrigH);
                    }
                    return s_origSetWindowPos(hWnd, hWndInsertAfter, X, Y, cx, cy, uFlags);
                }
                else if (parent == s_viewHwnd)
                {
                    if ((uFlags & SWP_NOMOVE) == 0)
                    {
                        X = DstX + (int)Math.Round((double)X * TargetW / 800.0);
                        Y = DstY + (int)Math.Round((double)Y * TargetH / 600.0);
                    }
                    if ((uFlags & SWP_NOSIZE) == 0)
                    {
                        cx = (int)Math.Round((double)cx * TargetW / 800.0);
                        cy = (int)Math.Round((double)cy * TargetH / 600.0);
                    }
                    return s_origSetWindowPos(hWnd, hWndInsertAfter, X, Y, cx, cy, uFlags);
                }
            }

            return s_origSetWindowPos(hWnd, hWndInsertAfter, X, Y, cx, cy, uFlags);
        }

        private static IntPtr MyDeferWindowPosHook(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags)
        {
            Log("DeferWindowPos: hWnd=0x{0:X}, pos=({1},{2}), size=({3}x{4}), flags=0x{5:X4} (view=0x{6:X}, game=0x{7:X})",
                hWnd.ToInt32(), x, y, cx, cy, uFlags, s_viewHwnd.ToInt32(), s_gameHwnd.ToInt32());

            if (hWnd == s_viewHwnd)
            {
                EnsurePresentationConfigured();
                if (WindowW > 0 && WindowH > 0)
                {
                    x = 0;
                    y = 0;
                    cx = WindowW;
                    cy = WindowH;
                    uFlags &= ~SWP_NOMOVE;
                    uFlags &= ~SWP_NOSIZE;
                }
                return s_origDeferWindowPos(hWinPosInfo, hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);
            }

            if (hWnd == s_gameHwnd)
            {
                EnsurePresentationConfigured();
                if (s_mode == DisplayMode.Fullscreen)
                {
                    int screenW = Screen.PrimaryScreen.Bounds.Width;
                    int screenH = Screen.PrimaryScreen.Bounds.Height;
                    x = 0;
                    y = 0;
                    cx = screenW;
                    cy = screenH;
                    uFlags &= ~SWP_NOMOVE;
                    uFlags &= ~SWP_NOSIZE;
                    return s_origDeferWindowPos(hWinPosInfo, hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);
                }
                else if (WindowW > 0 && WindowH > 0)
                {
                    if ((uFlags & SWP_NOSIZE) == 0)
                    {
                        cx = WindowW;
                        cy = WindowH;
                    }
                    return s_origDeferWindowPos(hWinPosInfo, hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);
                }
            }

            IntPtr parent = GetParent(hWnd);
            if (parent == s_viewHwnd && TargetW > 0 && TargetH > 0)
            {
                if ((uFlags & SWP_NOMOVE) == 0)
                {
                    x = DstX + (int)Math.Round((double)x * TargetW / 800.0);
                    y = DstY + (int)Math.Round((double)y * TargetH / 600.0);
                }
                if ((uFlags & SWP_NOSIZE) == 0)
                {
                    cx = (int)Math.Round((double)cx * TargetW / 800.0);
                    cy = (int)Math.Round((double)cy * TargetH / 600.0);
                }
                return s_origDeferWindowPos(hWinPosInfo, hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);
            }

            return s_origDeferWindowPos(hWinPosInfo, hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);
        }

        private static bool MyClientToScreenHook(IntPtr hWnd, ref POINT lpPoint)
        {
            if (hWnd != IntPtr.Zero)
            {
                McWindowInfo mc;
                if (s_mcWindows.TryGetValue(hWnd, out mc))
                {
                    lpPoint.X = (int)Math.Round((double)lpPoint.X * mc.PhysW / mc.OrigW);
                    lpPoint.Y = (int)Math.Round((double)lpPoint.Y * mc.PhysH / mc.OrigH);
                    return s_origClientToScreen(hWnd, ref lpPoint);
                }

                if ((hWnd == s_viewHwnd || hWnd == s_gameHwnd) && TargetW > 0 && TargetH > 0)
                {
                    lpPoint.X = DstX + (int)Math.Round((double)lpPoint.X * TargetW / 800.0);
                    lpPoint.Y = DstY + (int)Math.Round((double)lpPoint.Y * TargetH / 600.0);
                }
            }
            return s_origClientToScreen(hWnd, ref lpPoint);
        }

        private static void EnsurePresentationConfigured()
        {
            try
            {
                int screenW = Screen.PrimaryScreen.Bounds.Width;
                int screenH = Screen.PrimaryScreen.Bounds.Height;

                int targetClientW = screenW;
                int targetClientH = screenH;

                if (s_mode == DisplayMode.Fullscreen)
                {
                    targetClientW = screenW;
                    targetClientH = screenH;
                }
                else
                {
                    int winH = Math.Min(screenH - 100, 1200);
                    int winW = winH * 4 / 3;
                    targetClientW = winW;
                    targetClientH = winH;
                }

                if (WindowW != targetClientW || WindowH != targetClientH)
                {
                    RecalculateGeometry(targetClientW, targetClientH);
                }

                // If s_gameHwnd is not known yet but s_viewHwnd is, discover it from parent
                if (s_gameHwnd == IntPtr.Zero && s_viewHwnd != IntPtr.Zero)
                {
                    IntPtr p = GetParent(s_viewHwnd);
                    if (p != IntPtr.Zero)
                    {
                        s_gameHwnd = p;
                        Log("Discovered s_gameHwnd 0x{0:X} from s_viewHwnd parent", s_gameHwnd.ToInt32());
                    }
                }

                // Configure Game Window (s_gameHwnd)
                if (s_gameHwnd != IntPtr.Zero && !s_gameConfigured)
                {
                    Log("Configuring s_gameHwnd 0x{0:X} for {1} mode", s_gameHwnd.ToInt32(), s_mode);
                    if (s_mode == DisplayMode.Fullscreen)
                    {
                        SetWindowLongA(s_gameHwnd, GWL_STYLE, (IntPtr)unchecked((int)(WS_POPUP | WS_VISIBLE)));
                        SetWindowPos(s_gameHwnd, IntPtr.Zero, 0, 0, screenW, screenH, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                    }
                    else
                    {
                        int x = (screenW - targetClientW) / 2;
                        int y = (screenH - targetClientH) / 2;
                        SetWindowLongA(s_gameHwnd, GWL_STYLE, (IntPtr)unchecked((int)(WS_OVERLAPPEDWINDOW | WS_VISIBLE)));
                        SetWindowPos(s_gameHwnd, IntPtr.Zero, x, y, targetClientW, targetClientH, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                    }

                    if (s_origGameWndProc == IntPtr.Zero)
                    {
                        IntPtr gameProcPtr = Marshal.GetFunctionPointerForDelegate(s_myGameWndProc);
                        s_origGameWndProc = SetWindowLongA(s_gameHwnd, GWL_WNDPROC, gameProcPtr);
                        Log("Subclassed s_gameHwnd 0x{0:X}, origWndProc=0x{1:X}", s_gameHwnd.ToInt32(), s_origGameWndProc.ToInt32());
                    }

                    s_gameConfigured = true;
                }

                // Configure View Window (s_viewHwnd)
                if (s_viewHwnd != IntPtr.Zero && !s_viewConfigured)
                {
                    Log("Configuring s_viewHwnd 0x{0:X} to {1}x{2}", s_viewHwnd.ToInt32(), targetClientW, targetClientH);
                    IntPtr vStyle = GetWindowLongA(s_viewHwnd, GWL_STYLE);
                    SetWindowLongA(s_viewHwnd, GWL_STYLE, (IntPtr)unchecked((int)(vStyle.ToInt32() | unchecked((int)(WS_CLIPSIBLINGS | WS_CLIPCHILDREN)))));
                    SetWindowPos(s_viewHwnd, (IntPtr)1, 0, 0, targetClientW, targetClientH, SWP_FRAMECHANGED | SWP_SHOWWINDOW);

                    if (s_origViewWndProc == IntPtr.Zero)
                    {
                        IntPtr viewProcPtr = Marshal.GetFunctionPointerForDelegate(s_myViewWndProc);
                        s_origViewWndProc = SetWindowLongA(s_viewHwnd, GWL_WNDPROC, viewProcPtr);
                        Log("Subclassed s_viewHwnd 0x{0:X}, origWndProc=0x{1:X}", s_viewHwnd.ToInt32(), s_origViewWndProc.ToInt32());
                    }

                    s_viewConfigured = true;
                }

                s_presentationConfigured = s_gameConfigured && s_viewConfigured;
                if (s_presentationConfigured)
                {
                    Log("Presentation active: Target={0}x{1} at ({2},{3})", TargetW, TargetH, DstX, DstY);
                }
            }
            catch (Exception ex)
            {
                Log("Exception in EnsurePresentationConfigured: {0}", ex);
            }
        }

        private static int s_scaledBlitCount = 0;

        private static bool MyBitBltHook(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop)
        {
            IntPtr hWnd = WindowFromDC(hdcDest);
            if (hWnd != IntPtr.Zero)
            {
                McWindowInfo mc;
                if (s_mcWindows.TryGetValue(hWnd, out mc))
                {
                    int srcW = nWidth;
                    int srcH = nHeight;
                    int dstX = (int)Math.Round((double)nXDest * mc.PhysW / mc.OrigW);
                    int dstY = (int)Math.Round((double)nYDest * mc.PhysH / mc.OrigH);
                    int dstRight = (int)Math.Round((double)(nXDest + srcW) * mc.PhysW / mc.OrigW);
                    int dstBottom = (int)Math.Round((double)(nYDest + srcH) * mc.PhysH / mc.OrigH);
                    int dstW = Math.Max(0, dstRight - dstX);
                    int dstH = Math.Max(0, dstBottom - dstY);

                    Log("AmbitMcWindow StretchBlt: hWnd=0x{0:X}, src=({1},{2},{3}x{4}) -> dst=({5},{6},{7}x{8})",
                        hWnd.ToInt32(), nXSrc, nYSrc, srcW, srcH, dstX, dstY, dstW, dstH);

                    SetStretchBltMode(hdcDest, 1); // COLORONCOLOR
                    SetBrushOrgEx(hdcDest, 0, 0, IntPtr.Zero);
                    bool res = StretchBlt(hdcDest, dstX, dstY, dstW, dstH, hdcSrc, nXSrc, nYSrc, srcW, srcH, dwRop);
                    if (!res)
                    {
                        Log("AmbitMcWindow StretchBlt FAILED! err={0}", Marshal.GetLastWin32Error());
                    }
                    return res;
                }

                if (hWnd == s_viewHwnd || hWnd == s_gameHwnd)
                {
                    EnsurePresentationConfigured();

                if (TargetW > 0 && TargetH > 0)
                {
                    int srcCanvasW = 800;
                    int srcCanvasH = 600;

                    if (hdcSrc != IntPtr.Zero)
                    {
                        IntPtr hBmp = GetCurrentObject(hdcSrc, OBJ_BITMAP);
                        BITMAP bmp;
                        if (hBmp != IntPtr.Zero && GetObject(hBmp, Marshal.SizeOf(typeof(BITMAP)), out bmp) != 0 && bmp.bmWidth > 0 && bmp.bmHeight > 0)
                        {
                            srcCanvasW = bmp.bmWidth;
                            srcCanvasH = bmp.bmHeight;
                        }
                    }

                    int srcX = nXSrc;
                    int srcY = nYSrc;
                    int srcW = nWidth;
                    int srcH = nHeight;

                    int dstX;
                    int dstY;
                    int dstW;
                    int dstH;

                    // The game's virtual presentation canvas is 800x600.
                    // Full frame presentation blits cover the 800x600 space.
                    bool isFullFrame = (nWidth >= 790 && nHeight >= 590);
                    if (isFullFrame)
                    {
                        srcX = 0;
                        srcY = 0;
                        srcW = (srcCanvasW > 0 ? srcCanvasW : 800);
                        srcH = (srcCanvasH > 0 ? srcCanvasH : 600);
                        dstX = DstX;
                        dstY = DstY;
                        dstW = TargetW;
                        dstH = TargetH;

                        if (s_cachedCanvasDC == IntPtr.Zero && hdcSrc != IntPtr.Zero)
                        {
                            s_cachedCanvasDC = CreateCompatibleDC(hdcSrc);
                            s_cachedCanvasBmp = CreateCompatibleBitmap(hdcSrc, 800, 600);
                            SelectObject(s_cachedCanvasDC, s_cachedCanvasBmp);
                        }
                        if (s_cachedCanvasDC != IntPtr.Zero && hdcSrc != IntPtr.Zero)
                        {
                            s_origBitBlt(s_cachedCanvasDC, 0, 0, 800, 600, hdcSrc, 0, 0, SRCCOPY);
                        }
                    }
                    else
                    {
                        // Partial dirty rectangle blit.
                        if (srcW <= 0 || srcH <= 0) return true;

                        if (s_cachedCanvasDC != IntPtr.Zero && hdcSrc != IntPtr.Zero && nWidth > 0 && nHeight > 0)
                        {
                            s_origBitBlt(s_cachedCanvasDC, nXDest, nYDest, nWidth, nHeight, hdcSrc, nXSrc, nYSrc, dwRop);
                        }

                        // Check if destination coordinates are ALREADY in physical presentation space:
                        bool alreadyPhysical = (nXDest >= DstX && (nXDest + nWidth) <= (DstX + TargetW + 16) && (nXDest > 800 || nYDest > 600));
                        if (alreadyPhysical)
                        {
                            dstX = nXDest;
                            dstY = nYDest;
                            dstW = nWidth;
                            dstH = nHeight;
                        }
                        else
                        {
                            if (srcX + srcW > srcCanvasW) srcW = Math.Max(0, srcCanvasW - srcX);
                            if (srcY + srcH > srcCanvasH) srcH = Math.Max(0, srcCanvasH - srcY);

                            dstX = DstX + (int)Math.Round((double)nXDest * TargetW / 800.0);
                            dstY = DstY + (int)Math.Round((double)nYDest * TargetH / 600.0);
                            int dstRight = DstX + (int)Math.Round((double)(nXDest + srcW) * TargetW / 800.0);
                            int dstBottom = DstY + (int)Math.Round((double)(nYDest + srcH) * TargetH / 600.0);
                            dstW = Math.Max(0, dstRight - dstX);
                            dstH = Math.Max(0, dstBottom - dstY);
                        }
                    }

                    // Clamp blit destination within presentation bounds to protect pillarboxes
                    if (dstX < DstX)
                    {
                        int cut = DstX - dstX;
                        dstX = DstX;
                        dstW = Math.Max(0, dstW - cut);
                    }
                    if (dstY < DstY)
                    {
                        int cut = DstY - dstY;
                        dstY = DstY;
                        dstH = Math.Max(0, dstH - cut);
                    }
                    if (dstX + dstW > DstX + TargetW)
                    {
                        dstW = Math.Max(0, DstX + TargetW - dstX);
                    }
                    if (dstY + dstH > DstY + TargetH)
                    {
                        dstH = Math.Max(0, DstY + TargetH - dstY);
                    }
                    if (dstW <= 0 || dstH <= 0) return true;

                    if (s_scaledBlitCount < 50000)
                    {
                        s_scaledBlitCount++;
                        Log("Presentation StretchBlt #{0}: hdcSrc=0x{1:X} (canvas {2}x{3}), src=({4},{5},{6}x{7}) -> dst=({8},{9},{10}x{11}) on hWnd=0x{12:X}",
                            s_scaledBlitCount, hdcSrc.ToInt32(), srcCanvasW, srcCanvasH, srcX, srcY, srcW, srcH, dstX, dstY, dstW, dstH, hWnd.ToInt32());
                    }

                    // Ensure pillarboxes are black on initial frames
                    if (s_scaledBlitCount <= 3 && DstX > 0)
                    {
                        IntPtr blackBrush = GetStockObject(BLACK_BRUSH);
                        RECT leftPillar = new RECT { Left = 0, Top = 0, Right = DstX, Bottom = WindowH };
                        RECT rightPillar = new RECT { Left = DstX + TargetW, Top = 0, Right = WindowW, Bottom = WindowH };
                        FillRect(hdcDest, ref leftPillar, blackBrush);
                        FillRect(hdcDest, ref rightPillar, blackBrush);
                    }

                    // Detect unit sprite sheets (e.g. 7040x64 animation strip) blitted directly to the presentation window.
                    // Transparent pixels (background) in the sprite frame must NOT overwrite the terrain with solid black.
                    bool isSpriteSheet = (srcCanvasW > 800 && srcCanvasH <= 128 && srcW <= 128 && srcH <= 128);
                    if (isSpriteSheet)
                    {
                        uint crTransparent = 0x00000000; // default black (0x00BBGGRR)
                        uint corner = GetPixel(hdcSrc, srcX, srcY);
                        if (corner != 0xFFFFFFFF)
                        {
                            crTransparent = corner;
                        }

                        // Nearest-neighbor sampling avoids fringing/blending with transparent color
                        SetStretchBltMode(hdcDest, 3); // COLORONCOLOR
                        SetBrushOrgEx(hdcDest, 0, 0, IntPtr.Zero);
                        bool transRes = TransparentBlt(hdcDest, dstX, dstY, dstW, dstH, hdcSrc, srcX, srcY, srcW, srcH, crTransparent);
                        SetStretchBltMode(hdcDest, s_filter);

                        if (transRes)
                        {
                            Log("Sprite TransparentBlt #{0}: crTransparent=0x{1:X6}, src=({2},{3},{4}x{5}) -> dst=({6},{7},{8}x{9})",
                                s_scaledBlitCount, crTransparent, srcX, srcY, srcW, srcH, dstX, dstY, dstW, dstH);
                            return true;
                        }
                        Log("Sprite TransparentBlt FAILED! err={0}, falling back to StretchBlt", Marshal.GetLastWin32Error());
                    }

                    int bltFilter = (s_zoomLevel < 1.0f && s_zoomFilter == ZoomFilter.Nearest) ? COLORONCOLOR : s_filter;
                    SetStretchBltMode(hdcDest, bltFilter);
                    SetBrushOrgEx(hdcDest, 0, 0, IntPtr.Zero);
                    bool res = StretchBlt(hdcDest, dstX, dstY, dstW, dstH, hdcSrc, srcX, srcY, srcW, srcH, dwRop);
                    if (!res)
                    {
                        Log("Presentation StretchBlt FAILED! err={0}", Marshal.GetLastWin32Error());
                    }

                    // Render interactive world map viewport zoom when active
                    if (res && s_zoomEnabled)
                    {
                        if (s_zoomLevel > 1.0f)
                        {
                            bool intersectsViewport = isFullFrame || (nXDest < 586 && (nXDest + nWidth) > 8 && nYDest < 570 && (nYDest + nHeight) > 58);
                            if (intersectsViewport)
                            {
                                RenderZoomedViewport(hdcDest, (hdcSrc != IntPtr.Zero ? hdcSrc : s_cachedCanvasDC));
                            }
                        }
                        else if (s_zoomLevel < 1.0f)
                        {
                            bool intersectsViewport = isFullFrame || (nXDest < 586 && (nXDest + nWidth) > 8 && nYDest < 570 && (nYDest + nHeight) > 58);
                            if (intersectsViewport)
                            {
                                RenderCapturedLabels(hdcDest);
                            }
                        }
                    }

                    return res;
                }
            }
        }
        else if (hdcSrc != IntPtr.Zero)
        {
            IntPtr hSrcWnd = WindowFromDC(hdcSrc);
            if (hSrcWnd == s_viewHwnd || hSrcWnd == s_gameHwnd)
            {
                EnsurePresentationConfigured();
                if (TargetW > 0 && TargetH > 0)
                {
                    int readSrcX = DstX + (int)Math.Round((double)nXSrc * TargetW / 800.0);
                    int readSrcY = DstY + (int)Math.Round((double)nYSrc * TargetH / 600.0);
                    int readSrcW = (int)Math.Round((double)nWidth * TargetW / 800.0);
                    int readSrcH = (int)Math.Round((double)nHeight * TargetH / 600.0);

                    Log("Presentation Screen Read: src=({0},{1},{2}x{3}) [scaled ({4},{5},{6}x{7})] -> dst=({8},{9},{10}x{11})",
                        nXSrc, nYSrc, nWidth, nHeight, readSrcX, readSrcY, readSrcW, readSrcH, nXDest, nYDest, nWidth, nHeight);

                    SetStretchBltMode(hdcDest, 3); // COLORONCOLOR
                    SetBrushOrgEx(hdcDest, 0, 0, IntPtr.Zero);
                    return StretchBlt(hdcDest, nXDest, nYDest, nWidth, nHeight, hdcSrc, readSrcX, readSrcY, readSrcW, readSrcH, dwRop);
                }
            }
        }

        return s_origBitBlt(hdcDest, nXDest, nYDest, nWidth, nHeight, hdcSrc, nXSrc, nYSrc, dwRop);
    }

        private static void RenderZoomedViewport(IntPtr hdcDest, IntPtr hdcCanvas)
        {
            if (TargetW <= 0 || TargetH <= 0 || s_zoomLevel <= 1.0f || hdcCanvas == IntPtr.Zero) return;

            int vpDstX = DstX + (int)Math.Round(8.0 * TargetW / 800.0);
            int vpDstY = DstY + (int)Math.Round(58.0 * TargetH / 600.0);
            int vpDstW = (int)Math.Round(578.0 * TargetW / 800.0);
            int vpDstH = (int)Math.Round(512.0 * TargetH / 600.0);

            int zoomSrcW = (int)Math.Round(578.0 / s_zoomLevel);
            int zoomSrcH = (int)Math.Round(512.0 / s_zoomLevel);

            int sx = s_zoomFocalX - zoomSrcW / 2;
            int sy = s_zoomFocalY - zoomSrcH / 2;

            if (sx < 8) sx = 8;
            if (sx + zoomSrcW > 586) sx = 586 - zoomSrcW;
            if (sy < 58) sy = 58;
            if (sy + zoomSrcH > 570) sy = 570 - zoomSrcH;

            s_zoomEffectiveSx = sx;
            s_zoomEffectiveSy = sy;
            s_zoomEffectiveSrcW = zoomSrcW;
            s_zoomEffectiveSrcH = zoomSrcH;

            SetStretchBltMode(hdcDest, s_filter);
            SetBrushOrgEx(hdcDest, 0, 0, IntPtr.Zero);
            StretchBlt(hdcDest, vpDstX, vpDstY, vpDstW, vpDstH, hdcCanvas, sx, sy, zoomSrcW, zoomSrcH, SRCCOPY);
        }

        private static void RedrawZoomedView(IntPtr hWnd)
        {
            if (s_cachedCanvasDC != IntPtr.Zero && s_viewHwnd != IntPtr.Zero)
            {
                IntPtr hdcWin = GetDC(s_viewHwnd);
                if (hdcWin != IntPtr.Zero)
                {
                    if (s_zoomLevel <= 1.0f)
                    {
                        SetStretchBltMode(hdcWin, s_filter);
                        SetBrushOrgEx(hdcWin, 0, 0, IntPtr.Zero);
                        StretchBlt(hdcWin, DstX, DstY, TargetW, TargetH, s_cachedCanvasDC, 0, 0, 800, 600, SRCCOPY);
                        if (s_zoomLevel < 1.0f)
                        {
                            RenderCapturedLabels(hdcWin);
                        }
                    }
                    else
                    {
                        RenderZoomedViewport(hdcWin, s_cachedCanvasDC);
                    }
                    ReleaseDC(s_viewHwnd, hdcWin);
                }
            }
            if (hWnd != IntPtr.Zero)
            {
                InvalidateRect(hWnd, IntPtr.Zero, false);
            }
        }

        public static void TriggerMapRedraw(IntPtr hWnd)
        {
            if (s_zoomLevel <= 1.0f)
            {
                if (hWnd != IntPtr.Zero)
                {
                    InvalidateRect(hWnd, IntPtr.Zero, false);
                    UpdateWindow(hWnd);
                }
                else if (s_viewHwnd != IntPtr.Zero)
                {
                    InvalidateRect(s_viewHwnd, IntPtr.Zero, false);
                    UpdateWindow(s_viewHwnd);
                }
            }
            else
            {
                RedrawZoomedView(hWnd);
            }
        }

        public static void MapScreenToTileCoordinates(int inX, int inY, out int outX, out int outY)
        {
            if (s_lastScaleX > 0 && s_lastScaleY > 0)
            {
                double bufX = s_lastSx0 + inX * s_lastScaleX;
                double bufY = s_lastSy0 + inY * s_lastScaleY;
                outX = (int)Math.Round(bufX - 64.0);
                outY = (int)Math.Round(bufY - 64.0);
            }
            else
            {
                outX = inX;
                outY = inY;
            }
        }

        private static void MyMapDrawSelectionHook(IntPtr thisPtr)
        {
            if (s_origMapDrawSelection != null)
            {
                s_origMapDrawSelection(thisPtr);
            }
        }

        private static void MyMapDrawSelectionBoxHook(IntPtr thisPtr, IntPtr pArray)
        {
            if (s_zoomLevel < 1.0f)
            {
                // When zoomed out, suppress unscaled 1.0x selection brackets drawn at (350, 330) in the ocean
                return;
            }

            if (s_origMapDrawSelectionBox != null)
            {
                s_origMapDrawSelectionBox(thisPtr, pArray);
            }
        }

        private static void MyMapScreenToTileHook(IntPtr thisPtr, IntPtr pPt, IntPtr pOutCol, IntPtr pOutRow, IntPtr pOutValid)
        {
            if (s_origMapScreenToTile != null)
            {
                s_origMapScreenToTile(thisPtr, pPt, pOutCol, pOutRow, pOutValid);
            }
        }

        private static void MyMapDialogDrawHook(IntPtr thisPtr, IntPtr pRect)
        {
            if (s_pActiveMapDialog != thisPtr)
            {
                s_pActiveMapDialog = thisPtr;
                s_needCacheFlush = true;
                lock (s_labelLock)
                {
                    s_cityLabels.Clear();
                    s_capturedLabels.Clear();
                }
            }

            if (s_zoomLevel >= 1.0f || pRect == IntPtr.Zero)
            {
                s_origMapDialogDraw(thisPtr, pRect);
                return;
            }

            if (s_needCacheFlush)
            {
                s_needCacheFlush = false;
                FlushTileCache();
            }

            // Reset safe round-robin tile cache allocator counter for this frame
            if (s_pStubCounter != IntPtr.Zero)
            {
                Marshal.WriteInt32(s_pStubCounter, 0);
            }

            // Clear frame-specific tactical buffer labels (e.g. Sea names)
            lock (s_labelLock)
            {
                s_capturedLabels.Clear();
            }

            int zoomW = (int)Math.Ceiling(576.0 / (double)s_zoomLevel);
            int zoomH = (int)Math.Ceiling(512.0 / (double)s_zoomLevel);
            if (zoomW > 2380) zoomW = 2380;
            if (zoomH > 2180) zoomH = 2180;

            VRect expRect = new VRect
            {
                Left = 10,
                Top = 58,
                Right = 10 + zoomW,
                Bottom = 58 + zoomH
            };

            Marshal.StructureToPtr(expRect, s_pExpandedRectBuf, false);
            s_isInsideMapDraw = true;
            try
            {
                s_origMapDialogDraw(thisPtr, s_pExpandedRectBuf);
            }
            finally
            {
                s_isInsideMapDraw = false;
            }
        }

        private static void FlushTileCache()
        {
            try
            {
                if (s_pActiveMapDialog != IntPtr.Zero)
                {
                    for (int s = 0; s < 110; s++)
                    {
                        Marshal.WriteByte(s_pActiveMapDialog, s * 8 + 0x84, 0);
                    }
                }

                IntPtr pMapData = Marshal.ReadIntPtr((IntPtr)0x007606F4);
                if (pMapData != IntPtr.Zero)
                {
                    long addrMap = pMapData.ToInt64();
                    if (addrMap >= 0x10000 && addrMap < 0x7FFF0000)
                    {
                        IntPtr pTiles = Marshal.ReadIntPtr(pMapData, 0x10);
                        if (pTiles != IntPtr.Zero)
                        {
                            long addrTiles = pTiles.ToInt64();
                            if (addrTiles >= 0x10000 && addrTiles < 0x7FFF0000)
                            {
                                for (int t = 0; t < 6480; t++)
                                {
                                    Marshal.WriteByte(pTiles, t * 40 + 0x13, 0xFF);
                                }
                            }
                        }
                    }
                }
                Log("Flushed tactical map tile cache (110 dialog slots + 6480 tile records).");
            }
            catch (Exception ex)
            {
                Log("FlushTileCache error: {0}", ex.Message);
            }
        }

        public static void ProjectTileToBuffer(int tileIndex, int camX, int camY, out int bufX, out int bufY)
        {
            int row = tileIndex / 108;
            int col = tileIndex % 108;

            int outY = (row * 64) - camY;
            int outX = (col * 64) - camX;

            if ((row & 1) != 0)
            {
                outX += 32;
                if (outX >= 6880)
                {
                    outX -= 6912;
                }
            }

            while (outX < -64)
            {
                outX += 6912;
            }

            bufX = outX + 64;
            bufY = outY + 64;
        }

        private static void MyMapCopyBitsHook(IntPtr srcPixMap, IntPtr dstPixMap, IntPtr pSrcRect, IntPtr pDstRect, int mode, IntPtr maskRgn)
        {
            if (s_zoomLevel >= 1.0f || srcPixMap == IntPtr.Zero || dstPixMap == IntPtr.Zero || pSrcRect == IntPtr.Zero)
            {
                s_origCopyBits(srcPixMap, dstPixMap, pSrcRect, pDstRect, mode, maskRgn);
                return;
            }

            DownsampleMapBuffer(srcPixMap, dstPixMap, pSrcRect, pDstRect);
        }

        private static void MyMapSelectionCopyBitsHook(IntPtr srcPixMap, IntPtr dstPixMap, IntPtr pSrcRect, IntPtr pDstRect, int mode, IntPtr maskRgn)
        {
            if (s_zoomLevel >= 1.0f || srcPixMap == IntPtr.Zero || dstPixMap == IntPtr.Zero || pSrcRect == IntPtr.Zero)
            {
                if (s_origCopyBits != null)
                {
                    s_origCopyBits(srcPixMap, dstPixMap, pSrcRect, pDstRect, mode, maskRgn);
                }
                return;
            }

            try
            {
                // Read source rect from tactical buffer (QuickDraw Rect order: top, left, bottom, right)
                int r0 = Marshal.ReadInt32(pSrcRect, 0);
                int r1 = Marshal.ReadInt32(pSrcRect, 4);
                int r2 = Marshal.ReadInt32(pSrcRect, 8);
                int r3 = Marshal.ReadInt32(pSrcRect, 12);

                int top = Math.Min(r0, r2);
                int bottom = Math.Max(r0, r2);
                int left = Math.Min(r1, r3);
                int right = Math.Max(r1, r3);

                int srcW = right - left;
                int srcH = bottom - top;

                if (srcW > 0 && srcH > 0 && s_lastScaleX > 0 && s_lastScaleY > 0)
                {
                    int canvasX = 10 + (int)Math.Round((left - s_lastSx0) / s_lastScaleX);
                    int canvasY = 58 + (int)Math.Round((top - s_lastSy0) / s_lastScaleY);
                    int canvasW = Math.Max(1, (int)Math.Round((double)srcW / s_lastScaleX));
                    int canvasH = Math.Max(1, (int)Math.Round((double)srcH / s_lastScaleY));

                    if (canvasX >= 10 && (canvasX + canvasW) <= 586 && canvasY >= 58 && (canvasY + canvasH) <= 570)
                    {
                        DownsamplePatch(srcPixMap, dstPixMap, left, top, srcW, srcH, canvasX, canvasY, canvasW, canvasH);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("MyMapSelectionCopyBitsHook error: {0}", ex.Message);
            }

            // Note: We do NOT call s_origCopyBits with pDstRect when s_zoomLevel < 1.0f.
            // Calling it would stamp the unscaled 64x64 tile into Ross Channel (the ocean)!
        }

        private static unsafe void DownsamplePatch(
            IntPtr srcPixMap, IntPtr dstPixMap,
            int srcLeft, int srcTop, int srcW, int srcH,
            int canvasX, int canvasY, int canvasW, int canvasH)
        {
            try
            {
                IntPtr pSrcDib = Marshal.ReadIntPtr(srcPixMap, 0x1C);
                IntPtr pDstDib = Marshal.ReadIntPtr(dstPixMap, 0x1C);
                if (pSrcDib == IntPtr.Zero || pDstDib == IntPtr.Zero) return;

                IntPtr pSrcBits = Marshal.ReadIntPtr(pSrcDib, 0x0C);
                IntPtr pSrcBmi = Marshal.ReadIntPtr(pSrcDib, 0x10);
                IntPtr pDstBits = Marshal.ReadIntPtr(pDstDib, 0x0C);
                IntPtr pDstBmi = Marshal.ReadIntPtr(pDstDib, 0x10);
                if (pSrcBits == IntPtr.Zero || pDstBits == IntPtr.Zero || pSrcBmi == IntPtr.Zero || pDstBmi == IntPtr.Zero) return;

                int srcBufW = Marshal.ReadInt32(pSrcBmi, 4);
                int srcBufH = Marshal.ReadInt32(pSrcBmi, 8);
                int dstBufW = Marshal.ReadInt32(pDstBmi, 4);
                int dstBufH = Marshal.ReadInt32(pDstBmi, 8);

                int srcStride = ((srcBufW * 2 + 3) / 4) * 4;
                int dstStride = ((dstBufW * 2 + 3) / 4) * 4;

                bool srcBottomUp = srcBufH > 0;
                bool dstBottomUp = dstBufH > 0;
                int absSrcH = Math.Abs(srcBufH);
                int absDstH = Math.Abs(dstBufH);

                byte* pSrcBase = (byte*)pSrcBits.ToPointer();
                byte* pDstBase = (byte*)pDstBits.ToPointer();

                for (int dy = 0; dy < canvasH; dy++)
                {
                    int targetY = canvasY + dy;
                    if (targetY < 0 || targetY >= absDstH) continue;

                    int sourceY = srcTop + (int)((double)dy * srcH / canvasH);
                    if (sourceY < 0 || sourceY >= absSrcH) continue;

                    int srcScanY = srcBottomUp ? (absSrcH - 1 - sourceY) : sourceY;
                    int dstScanY = dstBottomUp ? (absDstH - 1 - targetY) : targetY;

                    ushort* pSrcLine = (ushort*)(pSrcBase + srcScanY * srcStride);
                    ushort* pDstLine = (ushort*)(pDstBase + dstScanY * dstStride);

                    for (int dx = 0; dx < canvasW; dx++)
                    {
                        int targetX = canvasX + dx;
                        if (targetX < 0 || targetX >= dstBufW) continue;

                        int sourceX = srcLeft + (int)((double)dx * srcW / canvasW);
                        if (sourceX < 0 || sourceX >= srcBufW) continue;

                        pDstLine[targetX] = pSrcLine[sourceX];
                    }
                }

                // Update s_cachedCanvasDC at the dirty rectangle
                if (s_cachedCanvasDC != IntPtr.Zero)
                {
                    SetStretchBltMode(s_cachedCanvasDC, s_filter);
                    SetBrushOrgEx(s_cachedCanvasDC, 0, 0, IntPtr.Zero);
                    int srcDibY = dstBottomUp ? (absDstH - (canvasY + canvasH)) : canvasY;
                    StretchDIBits(s_cachedCanvasDC, canvasX, canvasY, canvasW, canvasH,
                        canvasX, srcDibY, canvasW, canvasH,
                        pDstBits, pDstBmi, 0 /* DIB_RGB_COLORS */, SRCCOPY);
                }

                // Present dirty tile to presentation window
                if (s_viewHwnd != IntPtr.Zero && TargetW > 0 && TargetH > 0)
                {
                    int dstX = DstX + (int)Math.Round((double)canvasX * TargetW / 800.0);
                    int dstY = DstY + (int)Math.Round((double)canvasY * TargetH / 600.0);
                    int dstRight = DstX + (int)Math.Round((double)(canvasX + canvasW) * TargetW / 800.0);
                    int dstBottom = DstY + (int)Math.Round((double)(canvasY + canvasH) * TargetH / 600.0);
                    int dstW = Math.Max(1, dstRight - dstX);
                    int dstH = Math.Max(1, dstBottom - dstY);

                    IntPtr hdcWin = GetDC(s_viewHwnd);
                    if (hdcWin != IntPtr.Zero)
                    {
                        SetStretchBltMode(hdcWin, s_filter);
                        SetBrushOrgEx(hdcWin, 0, 0, IntPtr.Zero);
                        int srcDibY = dstBottomUp ? (absDstH - (canvasY + canvasH)) : canvasY;
                        StretchDIBits(hdcWin, dstX, dstY, dstW, dstH,
                            canvasX, srcDibY, canvasW, canvasH,
                            pDstBits, pDstBmi, 0 /* DIB_RGB_COLORS */, SRCCOPY);
                        ReleaseDC(s_viewHwnd, hdcWin);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("DownsamplePatch error: {0}", ex.Message);
            }
        }

        public static unsafe void DownsampleMapBuffer(IntPtr srcPixMap, IntPtr dstPixMap, IntPtr pSrcRect, IntPtr pDstRect)
        {
            try
            {
                IntPtr pSrcDib = Marshal.ReadIntPtr(srcPixMap, 0x1C);
                IntPtr pDstDib = Marshal.ReadIntPtr(dstPixMap, 0x1C);
                if (pSrcDib == IntPtr.Zero || pDstDib == IntPtr.Zero) return;

                IntPtr pSrcBits = Marshal.ReadIntPtr(pSrcDib, 0x0C);
                IntPtr pSrcBmi = Marshal.ReadIntPtr(pSrcDib, 0x10);
                IntPtr pDstBits = Marshal.ReadIntPtr(pDstDib, 0x0C);
                IntPtr pDstBmi = Marshal.ReadIntPtr(pDstDib, 0x10);
                if (pSrcBits == IntPtr.Zero || pDstBits == IntPtr.Zero || pSrcBmi == IntPtr.Zero || pDstBmi == IntPtr.Zero) return;

                int srcW = Marshal.ReadInt32(pSrcBmi, 4);
                int srcH = Marshal.ReadInt32(pSrcBmi, 8);
                int dstW = Marshal.ReadInt32(pDstBmi, 4);
                int dstH = Marshal.ReadInt32(pDstBmi, 8);

                int srcStride = ((srcW * 2 + 3) / 4) * 4;
                int dstStride = ((dstW * 2 + 3) / 4) * 4;

                bool is565 = false;
                int compression = Marshal.ReadInt32(pSrcBmi, 16);
                if (compression == 3) // BI_BITFIELDS
                {
                    uint gMask = (uint)Marshal.ReadInt32(pSrcBmi, 44);
                    if (gMask == 0x07E0) is565 = true;
                }

                int sx0 = Marshal.ReadInt32(pSrcRect, 0);
                int sy0 = Marshal.ReadInt32(pSrcRect, 4);
                int sx1 = Marshal.ReadInt32(pSrcRect, 8);
                int sy1 = Marshal.ReadInt32(pSrcRect, 12);
                int sw = sx1 - sx0;
                int sh = sy1 - sy0;
                if (sw <= 0 || sh <= 0) return;

                int dx0 = 10;
                int dy0 = 58;
                int dw = 576;
                int dh = 512;

                bool srcBottomUp = srcH > 0;
                bool dstBottomUp = dstH > 0;
                int absSrcH = Math.Abs(srcH);
                int absDstH = Math.Abs(dstH);

                byte* pSrcBase = (byte*)pSrcBits.ToPointer();
                byte* pDstBase = (byte*)pDstBits.ToPointer();

                double scaleX = (double)sw / dw;
                double scaleY = (double)sh / dh;

                // Cache viewport transformation parameters for label overlay
                s_lastSx0 = sx0;
                s_lastSy0 = sy0;
                s_lastScaleX = scaleX;
                s_lastScaleY = scaleY;

                if (s_zoomFilter == ZoomFilter.Nearest)
                {
                    // 1. Pure Point-Sampling: 100% crisp pixel art, zero blur
                    for (int y = 0; y < dh; y++)
                    {
                        int dstYActual = dy0 + y;
                        if (dstYActual < 0 || dstYActual >= absDstH) continue;
                        int dstScanY = dstBottomUp ? (absDstH - 1 - dstYActual) : dstYActual;
                        ushort* pDstRow = (ushort*)(pDstBase + dstScanY * dstStride);

                        int sy = (int)(sy0 + (y + 0.5) * scaleY);
                        if (sy < 0) sy = 0; else if (sy >= absSrcH) sy = absSrcH - 1;
                        int srcScanY = srcBottomUp ? (absSrcH - 1 - sy) : sy;
                        ushort* pSrcRow = (ushort*)(pSrcBase + srcScanY * srcStride);

                        for (int x = 0; x < dw; x++)
                        {
                            int dstXActual = dx0 + x;
                            if (dstXActual < 0 || dstXActual >= dstW) continue;

                            int sx = (int)(sx0 + (x + 0.5) * scaleX);
                            if (sx < 0) sx = 0; else if (sx >= srcW) sx = srcW - 1;

                            pDstRow[dstXActual] = pSrcRow[sx];
                        }
                    }
                }
                else if (s_zoomFilter == ZoomFilter.Sharp)
                {
                    // 2. Contrast-Preserved: 75% center point sample + 25% box average
                    for (int y = 0; y < dh; y++)
                    {
                        int dstYActual = dy0 + y;
                        if (dstYActual < 0 || dstYActual >= absDstH) continue;
                        int dstScanY = dstBottomUp ? (absDstH - 1 - dstYActual) : dstYActual;
                        ushort* pDstRow = (ushort*)(pDstBase + dstScanY * dstStride);

                        double srcStartY = sy0 + y * scaleY;
                        double srcEndY = sy0 + (y + 1) * scaleY;
                        int yStart = (int)srcStartY;
                        int yEnd = (int)Math.Ceiling(srcEndY);
                        if (yEnd <= yStart) yEnd = yStart + 1;

                        int centerSy = (int)(sy0 + (y + 0.5) * scaleY);
                        if (centerSy < 0) centerSy = 0; else if (centerSy >= absSrcH) centerSy = absSrcH - 1;
                        int centerScanY = srcBottomUp ? (absSrcH - 1 - centerSy) : centerSy;
                        ushort* pCenterRow = (ushort*)(pSrcBase + centerScanY * srcStride);

                        for (int x = 0; x < dw; x++)
                        {
                            int dstXActual = dx0 + x;
                            if (dstXActual < 0 || dstXActual >= dstW) continue;

                            double srcStartX = sx0 + x * scaleX;
                            double srcEndX = sx0 + (x + 1) * scaleX;
                            int xStart = (int)srcStartX;
                            int xEnd = (int)Math.Ceiling(srcEndX);
                            if (xEnd <= xStart) xEnd = xStart + 1;

                            int centerSx = (int)(sx0 + (x + 0.5) * scaleX);
                            if (centerSx < 0) centerSx = 0; else if (centerSx >= srcW) centerSx = srcW - 1;
                            ushort pCenter = pCenterRow[centerSx];

                            int cR = is565 ? ((pCenter >> 11) & 0x1F) : ((pCenter >> 10) & 0x1F);
                            int cG = is565 ? ((pCenter >> 5) & 0x3F) : ((pCenter >> 5) & 0x1F);
                            int cB = pCenter & 0x1F;

                            int totalR = 0, totalG = 0, totalB = 0;
                            int sampleCount = 0;

                            for (int sy = yStart; sy < yEnd; sy++)
                            {
                                if (sy < 0 || sy >= absSrcH) continue;
                                int srcScanY = srcBottomUp ? (absSrcH - 1 - sy) : sy;
                                ushort* pSrcRow = (ushort*)(pSrcBase + srcScanY * srcStride);

                                for (int sx = xStart; sx < xEnd; sx++)
                                {
                                    if (sx < 0 || sx >= srcW) continue;
                                    ushort pixel = pSrcRow[sx];

                                    if (is565)
                                    {
                                        totalR += (pixel >> 11) & 0x1F;
                                        totalG += (pixel >> 5) & 0x3F;
                                        totalB += pixel & 0x1F;
                                    }
                                    else
                                    {
                                        totalR += (pixel >> 10) & 0x1F;
                                        totalG += (pixel >> 5) & 0x1F;
                                        totalB += pixel & 0x1F;
                                    }
                                    sampleCount++;
                                }
                            }

                            if (sampleCount > 0)
                            {
                                int avgR = totalR / sampleCount;
                                int avgG = totalG / sampleCount;
                                int avgB = totalB / sampleCount;

                                int r = (cR * 3 + avgR + 2) / 4;
                                int g = (cG * 3 + avgG + 2) / 4;
                                int b = (cB * 3 + avgB + 2) / 4;

                                if (is565)
                                {
                                    pDstRow[dstXActual] = (ushort)((r << 11) | (g << 5) | b);
                                }
                                else
                                {
                                    pDstRow[dstXActual] = (ushort)((r << 10) | (g << 5) | b);
                                }
                            }
                        }
                    }
                }
                else
                {
                    // 3. Smooth Area-Averaging Box Filter
                    for (int y = 0; y < dh; y++)
                    {
                        int dstYActual = dy0 + y;
                        if (dstYActual < 0 || dstYActual >= absDstH) continue;
                        int dstScanY = dstBottomUp ? (absDstH - 1 - dstYActual) : dstYActual;
                        ushort* pDstRow = (ushort*)(pDstBase + dstScanY * dstStride);

                        double srcStartY = sy0 + y * scaleY;
                        double srcEndY = sy0 + (y + 1) * scaleY;
                        int yStart = (int)srcStartY;
                        int yEnd = (int)Math.Ceiling(srcEndY);
                        if (yEnd <= yStart) yEnd = yStart + 1;

                        for (int x = 0; x < dw; x++)
                        {
                            int dstXActual = dx0 + x;
                            if (dstXActual < 0 || dstXActual >= dstW) continue;

                            double srcStartX = sx0 + x * scaleX;
                            double srcEndX = sx0 + (x + 1) * scaleX;
                            int xStart = (int)srcStartX;
                            int xEnd = (int)Math.Ceiling(srcEndX);
                            if (xEnd <= xStart) xEnd = xStart + 1;

                            int totalR = 0, totalG = 0, totalB = 0;
                            int sampleCount = 0;

                            for (int sy = yStart; sy < yEnd; sy++)
                            {
                                if (sy < 0 || sy >= absSrcH) continue;
                                int srcScanY = srcBottomUp ? (absSrcH - 1 - sy) : sy;
                                ushort* pSrcRow = (ushort*)(pSrcBase + srcScanY * srcStride);

                                for (int sx = xStart; sx < xEnd; sx++)
                                {
                                    if (sx < 0 || sx >= srcW) continue;
                                    ushort pixel = pSrcRow[sx];

                                    if (is565)
                                    {
                                        totalR += (pixel >> 11) & 0x1F;
                                        totalG += (pixel >> 5) & 0x3F;
                                        totalB += pixel & 0x1F;
                                    }
                                    else
                                    {
                                        totalR += (pixel >> 10) & 0x1F;
                                        totalG += (pixel >> 5) & 0x1F;
                                        totalB += pixel & 0x1F;
                                    }
                                    sampleCount++;
                                }
                            }

                            if (sampleCount > 0)
                            {
                                int avgR = totalR / sampleCount;
                                int avgG = totalG / sampleCount;
                                int avgB = totalB / sampleCount;
                                if (is565)
                                {
                                    pDstRow[dstXActual] = (ushort)((avgR << 11) | (avgG << 5) | avgB);
                                }
                                else
                                {
                                    pDstRow[dstXActual] = (ushort)((avgR << 10) | (avgG << 5) | avgB);
                                }
                            }
                        }
                    }
                }

                Marshal.WriteInt32(pDstRect, 0, 10);
                Marshal.WriteInt32(pDstRect, 4, 58);
                Marshal.WriteInt32(pDstRect, 8, 586);
                Marshal.WriteInt32(pDstRect, 12, 570);

                if (s_cachedCanvasDC != IntPtr.Zero && s_cachedCanvasBmp != IntPtr.Zero)
                {
                    SetDIBits(s_cachedCanvasDC, s_cachedCanvasBmp, 0, (uint)absDstH, pDstBits, pDstBmi, 0);
                }
            }
            catch (Exception ex)
            {
                Log("DownsampleMapBuffer exception: {0}", ex);
            }
        }

        private static void OnMapDrawString(IntPtr pCString)
        {
            try
            {
                if (s_isInsideMapDraw && s_zoomLevel < 1.0f && pCString != IntPtr.Zero)
                {
                    IntPtr pStr = Marshal.ReadIntPtr(pCString);
                    if (pStr != IntPtr.Zero)
                    {
                        string rawText = Marshal.PtrToStringAnsi(pStr);
                        string text = rawText != null ? rawText.Trim() : null;
                        if (!string.IsNullOrEmpty(text) && text.Length >= 3 && HasLetter(text))
                        {
                            int penX = Marshal.ReadInt32((IntPtr)0x00759F30);
                            int penY = Marshal.ReadInt32((IntPtr)0x00759F34);

                            if (penY < 64 && s_pActiveMapDialog != IntPtr.Zero)
                            {
                                if (IsCityName(text))
                                {
                                    int slotIndex = penX / 64;
                                    if (slotIndex >= 0 && slotIndex < 110)
                                    {
                                        short tileIndex = Marshal.ReadInt16(s_pActiveMapDialog, slotIndex * 8 + 0x8A);
                                        if (tileIndex >= 0 && tileIndex < 6480)
                                        {
                                            lock (s_labelLock)
                                            {
                                                s_cityLabels[tileIndex] = new CityLabelRecord
                                                {
                                                    TileIndex = tileIndex,
                                                    Text = text,
                                                    LocalX = penX % 64,
                                                    LocalY = penY
                                                };

                                                if (s_cityLabels.Count <= 25)
                                                {
                                                    Log("Registered city label #{0}: '{1}' on tile {2} (slot {3}, pen=({4},{5}))",
                                                        s_cityLabels.Count, text, tileIndex, slotIndex, penX, penY);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            else if (penY >= 64)
                            {
                                if (IsOceanName(text))
                                {
                                    lock (s_labelLock)
                                    {
                                        bool duplicate = false;
                                        for (int i = 0; i < s_capturedLabels.Count; i++)
                                        {
                                            if (s_capturedLabels[i].Text == text &&
                                                Math.Abs(s_capturedLabels[i].PenX - penX) < 16 &&
                                                Math.Abs(s_capturedLabels[i].PenY - penY) < 16)
                                            {
                                                s_capturedLabels[i] = new CapturedLabel
                                                {
                                                    Text = text,
                                                    PenX = penX,
                                                    PenY = penY
                                                };
                                                duplicate = true;
                                                break;
                                            }
                                        }
                                        if (!duplicate)
                                        {
                                            s_capturedLabels.Add(new CapturedLabel
                                            {
                                                Text = text,
                                                PenX = penX,
                                                PenY = penY
                                            });

                                            if (s_capturedLabels.Count <= 20)
                                            {
                                                Log("Captured sea/map label #{0}: '{1}' at pen=({2},{3})",
                                                    s_capturedLabels.Count, text, penX, penY);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("OnMapDrawString error: {0}", ex.Message);
            }

            if (s_isInsideMapDraw && s_zoomLevel < 1.0f)
            {
                // When zoomed out, suppress native QuickDraw DrawString so text is NOT burned
                // into the GWorld bitmap (which would downsample into blurry background duplicates).
                // Crisp ClearType labels will be rendered exclusively by RenderCapturedLabels.
                return;
            }

            if (s_origDrawString != null)
            {
                s_origDrawString(pCString);
            }
        }

        private static bool HasLetter(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsLetter(s[i])) return true;
            }
            return false;
        }

        private static void DrawHaloText(IntPtr hdcDest, int screenX, int screenY, string text, int outlineDist)
        {
            // 1. Draw solid dark outline (8 directions) for maximum contrast against any terrain
            SetTextColor(hdcDest, 0x00000000); // Black (0x00BBGGRR)
            for (int ox = -outlineDist; ox <= outlineDist; ox += outlineDist)
            {
                for (int oy = -outlineDist; oy <= outlineDist; oy += outlineDist)
                {
                    if (ox != 0 || oy != 0)
                    {
                        TextOutA(hdcDest, screenX + ox, screenY + oy, text, text.Length);
                    }
                }
            }

            // 2. Draw crisp bright white interior text
            SetTextColor(hdcDest, 0x00FFFFFF); // Pure White
            TextOutA(hdcDest, screenX, screenY, text, text.Length);
        }

        private static void RenderCapturedLabels(IntPtr hdcDest)
        {
            try
            {
                if (hdcDest == IntPtr.Zero || s_zoomLevel >= 1.0f || TargetW <= 0 || TargetH <= 0) return;
                if (s_lastScaleX <= 0 || s_lastScaleY <= 0) return;

                double scaleXScreen = (double)TargetW / 800.0;
                double scaleYScreen = (double)TargetH / 600.0;
                if (scaleXScreen <= 0 || scaleYScreen <= 0) return;

                int vpLeft   = DstX + (int)Math.Round(10.0 * scaleXScreen);
                int vpTop    = DstY + (int)Math.Round(58.0 * scaleYScreen);
                int vpRight  = DstX + (int)Math.Round(586.0 * scaleXScreen);
                int vpBottom = DstY + (int)Math.Round(570.0 * scaleYScreen);

                // Font height scaled proportionally to presentation window height for crisp high-DPI display
                int fontH = Math.Max(11, (int)Math.Round(13.0 * scaleYScreen));

                IntPtr hFont = CreateFontA(
                    fontH, 0, 0, 0,
                    700, // FW_BOLD
                    0, 0, 0, 0,
                    0, 0,
                    5, // CLEARTYPE_QUALITY
                    0,
                    "Segoe UI"
                );

                IntPtr oldFont = SelectObject(hdcDest, hFont);
                int oldBkMode = SetBkMode(hdcDest, TRANSPARENT);
                uint oldTextAlign = SetTextAlign(hdcDest, TA_BASELINE | TA_CENTER);

                // Restrict drawing strictly within the tactical world map viewport
                IntPtr hRgn = CreateRectRgn(vpLeft, vpTop, vpRight, vpBottom);
                SelectClipRgn(hdcDest, hRgn);

                int outlineDist = Math.Max(1, (int)Math.Round(1.5 * scaleYScreen));

                short camX = (s_pActiveMapDialog != IntPtr.Zero) ? Marshal.ReadInt16(s_pActiveMapDialog, 0x64) : (short)0;
                short camY = (s_pActiveMapDialog != IntPtr.Zero) ? Marshal.ReadInt16(s_pActiveMapDialog, 0x68) : (short)0;

                // Collision bounding boxes of placed labels to guarantee zero overlapping text
                List<RECT> placedBoxes = new List<RECT>(128);

                // 1. Render registered city labels projected from world hex tiles
                List<CityLabelRecord> cityLabels;
                lock (s_labelLock)
                {
                    cityLabels = new List<CityLabelRecord>(s_cityLabels.Values);
                }

                // Sort: National capitals first, then stable deterministic ordering by TileIndex (prevents flickering)
                cityLabels.Sort((a, b) =>
                {
                    bool aCap = s_nationalCapitals.Contains(a.Text);
                    bool bCap = s_nationalCapitals.Contains(b.Text);
                    if (aCap != bCap) return aCap ? -1 : 1;
                    return a.TileIndex.CompareTo(b.TileIndex);
                });

                for (int i = 0; i < cityLabels.Count; i++)
                {
                    CityLabelRecord city = cityLabels[i];
                    if (string.IsNullOrEmpty(city.Text)) continue;

                    int bufX, bufY;
                    ProjectTileToBuffer(city.TileIndex, camX, camY, out bufX, out bufY);

                    // Test primary position and potential horizontal cylinder world wraps across expanded buffer
                    int[] testX = new int[] { bufX, bufX - 6912, bufX + 6912 };
                    for (int w = 0; w < testX.Length; w++)
                    {
                        int curBufX = testX[w];
                        if (curBufX < -64 || curBufX > 2528) continue;
                        if (bufY < -64 || bufY > 2368) continue;

                        int tileCenterX = curBufX + 32;
                        int tileBaselineY = bufY + 15;

                        double canvasCenterX = 10.0 + (tileCenterX - s_lastSx0) / s_lastScaleX;
                        double canvasBaselineY = 58.0 + (tileBaselineY - s_lastSy0) / s_lastScaleY;

                        int screenX = DstX + (int)Math.Round(canvasCenterX * scaleXScreen);
                        int screenY = DstY + (int)Math.Round(canvasBaselineY * scaleYScreen);

                        if (screenX >= vpLeft && screenX <= vpRight && screenY >= vpTop && screenY <= vpBottom)
                        {
                            SIZE textSize;
                            if (!GetTextExtentPoint32A(hdcDest, city.Text, city.Text.Length, out textSize))
                            {
                                textSize.cx = (int)Math.Round(city.Text.Length * 7.5 * scaleXScreen);
                                textSize.cy = fontH;
                            }

                            int halfW = (textSize.cx + 1) / 2;
                            int boxLeft   = screenX - halfW - outlineDist - 3;
                            int boxRight  = screenX + halfW + outlineDist + 3;
                            int boxTop    = screenY - textSize.cy - outlineDist - 2;
                            int boxBottom = screenY + outlineDist + 3;

                            // Collision check: skip label if it overlaps any previously placed label
                            bool collides = false;
                            for (int b = 0; b < placedBoxes.Count; b++)
                            {
                                RECT placed = placedBoxes[b];
                                if (boxLeft < placed.Right && boxRight > placed.Left &&
                                    boxTop < placed.Bottom && boxBottom > placed.Top)
                                {
                                    collides = true;
                                    break;
                                }
                            }

                            if (!collides)
                            {
                                DrawHaloText(hdcDest, screenX, screenY, city.Text, outlineDist);
                                placedBoxes.Add(new RECT { Left = boxLeft, Top = boxTop, Right = boxRight, Bottom = boxBottom });
                            }
                        }
                    }
                }

                // 2. Render direct tactical buffer labels (e.g. Sea names like "Ross Channel")
                List<CapturedLabel> seaLabels;
                lock (s_labelLock)
                {
                    seaLabels = new List<CapturedLabel>(s_capturedLabels);
                }

                for (int i = 0; i < seaLabels.Count; i++)
                {
                    CapturedLabel label = seaLabels[i];
                    if (string.IsNullOrEmpty(label.Text)) continue;

                    double origHalfWidth = label.Text.Length * 3.25;
                    double tileCenterX = label.PenX + origHalfWidth;

                    double canvasCenterX = 10.0 + (tileCenterX - s_lastSx0) / s_lastScaleX;
                    double canvasBaselineY = 58.0 + (label.PenY - s_lastSy0) / s_lastScaleY;

                    int screenX = DstX + (int)Math.Round(canvasCenterX * scaleXScreen);
                    int screenY = DstY + (int)Math.Round(canvasBaselineY * scaleYScreen);

                    if (screenX >= vpLeft && screenX <= vpRight && screenY >= vpTop && screenY <= vpBottom)
                    {
                        SIZE textSize;
                        if (!GetTextExtentPoint32A(hdcDest, label.Text, label.Text.Length, out textSize))
                        {
                            textSize.cx = (int)Math.Round(label.Text.Length * 7.5 * scaleXScreen);
                            textSize.cy = fontH;
                        }

                        int halfW = (textSize.cx + 1) / 2;
                        int boxLeft   = screenX - halfW - outlineDist - 3;
                        int boxRight  = screenX + halfW + outlineDist + 3;
                        int boxTop    = screenY - textSize.cy - outlineDist - 2;
                        int boxBottom = screenY + outlineDist + 3;

                        bool collides = false;
                        for (int b = 0; b < placedBoxes.Count; b++)
                        {
                            RECT placed = placedBoxes[b];
                            if (boxLeft < placed.Right && boxRight > placed.Left &&
                                boxTop < placed.Bottom && boxBottom > placed.Top)
                            {
                                collides = true;
                                break;
                            }
                        }

                        if (!collides)
                        {
                            DrawHaloText(hdcDest, screenX, screenY, label.Text, outlineDist);
                            placedBoxes.Add(new RECT { Left = boxLeft, Top = boxTop, Right = boxRight, Bottom = boxBottom });
                        }
                    }
                }

                SelectClipRgn(hdcDest, IntPtr.Zero);
                DeleteObject(hRgn);
                SetTextAlign(hdcDest, oldTextAlign);
                SetBkMode(hdcDest, oldBkMode);
                SelectObject(hdcDest, oldFont);
                DeleteObject(hFont);
            }
            catch (Exception ex)
            {
                Log("RenderCapturedLabels error: {0}", ex.Message);
            }
        }

        private static void InstallOptionBEngineHooks()
        {
            try
            {
                uint oldProtect;

                // 1. Unprotect & patch UpdateTheGWorld buffer bounds:
                // Expands tactical buffer from 864x728 to 2464x2304 (Right=2400, Bottom=2240)
                byte[] updateGWorldPatch = new byte[]
                {
                    0xBA, 0xC0, 0xFF, 0xFF, 0xFF,                      // mov edx, -64
                    0x89, 0x54, 0x24, 0x00,                            // mov [esp], edx (Left = -64)
                    0x89, 0x54, 0x24, 0x04,                            // mov [esp+4], edx (Top = -64)
                    0xC7, 0x44, 0x24, 0x08, 0x60, 0x09, 0x00, 0x00,    // mov dword ptr [esp+8], 2400 (Right = 2400)
                    0xC7, 0x44, 0x24, 0x0C, 0xC0, 0x08, 0x00, 0x00,    // mov dword ptr [esp+0xc], 2240 (Bottom = 2240)
                    0x90                                               // nop
                };
                VirtualProtect((IntPtr)CODE_UPDATETHEGWORLD_RECT, (UIntPtr)updateGWorldPatch.Length, PAGE_EXECUTE_READWRITE, out oldProtect);
                Marshal.Copy(updateGWorldPatch, 0, (IntPtr)CODE_UPDATETHEGWORLD_RECT, updateGWorldPatch.Length);
                VirtualProtect((IntPtr)CODE_UPDATETHEGWORLD_RECT, (UIntPtr)updateGWorldPatch.Length, oldProtect, out oldProtect);

                // 2. Hook Slot 69 (TMapDialog::Draw) in vtables
                int[] drawVtables = new int[] { VTABLE_TMAPDIALOG_DRAW, VTABLE_TNEWMAPDIALOG_DRAW, VTABLE_TMAPDIALOG3_DRAW };
                for (int i = 0; i < drawVtables.Length; i++)
                {
                    VirtualProtect((IntPtr)drawVtables[i], (UIntPtr)4, PAGE_EXECUTE_READWRITE, out oldProtect);
                    IntPtr origDrawPtr = Marshal.ReadIntPtr((IntPtr)drawVtables[i]);
                    if (s_origMapDialogDraw == null && origDrawPtr != IntPtr.Zero)
                    {
                        s_origMapDialogDraw = (MapDialogDrawDelegate)Marshal.GetDelegateForFunctionPointer(origDrawPtr, typeof(MapDialogDrawDelegate));
                    }
                    Marshal.WriteIntPtr((IntPtr)drawVtables[i], Marshal.GetFunctionPointerForDelegate(s_myMapDialogDraw));
                    VirtualProtect((IntPtr)drawVtables[i], (UIntPtr)4, oldProtect, out oldProtect);
                }

                // Hook Slot 110 (DrawSelection) in vtables
                int[] selVtables = new int[] { VTABLE_TMAPDIALOG_DRAW_SELECTION, VTABLE_TNEWMAPDIALOG_DRAW_SELECTION, VTABLE_TMAPDIALOG3_DRAW_SELECTION };
                for (int i = 0; i < selVtables.Length; i++)
                {
                    VirtualProtect((IntPtr)selVtables[i], (UIntPtr)4, PAGE_EXECUTE_READWRITE, out oldProtect);
                    IntPtr origSelPtr = Marshal.ReadIntPtr((IntPtr)selVtables[i]);
                    if (s_origMapDrawSelection == null && origSelPtr != IntPtr.Zero)
                    {
                        s_origMapDrawSelection = (MapDrawSelectionDelegate)Marshal.GetDelegateForFunctionPointer(origSelPtr, typeof(MapDrawSelectionDelegate));
                    }
                    Marshal.WriteIntPtr((IntPtr)selVtables[i], Marshal.GetFunctionPointerForDelegate(s_myMapDrawSelection));
                    VirtualProtect((IntPtr)selVtables[i], (UIntPtr)4, oldProtect, out oldProtect);
                }

                // Hook Slot 113 (ScreenToTile) in vtables
                int[] sttVtables = new int[] { VTABLE_TMAPDIALOG_SCREEN_TO_TILE, VTABLE_TNEWMAPDIALOG_SCREEN_TO_TILE, VTABLE_TMAPDIALOG3_SCREEN_TO_TILE };
                for (int i = 0; i < sttVtables.Length; i++)
                {
                    VirtualProtect((IntPtr)sttVtables[i], (UIntPtr)4, PAGE_EXECUTE_READWRITE, out oldProtect);
                    IntPtr origSttPtr = Marshal.ReadIntPtr((IntPtr)sttVtables[i]);
                    if (s_origMapScreenToTile == null && origSttPtr != IntPtr.Zero)
                    {
                        s_origMapScreenToTile = (MapScreenToTileDelegate)Marshal.GetDelegateForFunctionPointer(origSttPtr, typeof(MapScreenToTileDelegate));
                    }
                    Marshal.WriteIntPtr((IntPtr)sttVtables[i], Marshal.GetFunctionPointerForDelegate(s_myMapScreenToTile));
                    VirtualProtect((IntPtr)sttVtables[i], (UIntPtr)4, oldProtect, out oldProtect);
                }

                // 3. Install safe round-robin tile cache slot allocator stub at 0x0055D3E3:
                // Prevents 110-tile buffer overflow when rendering expanded viewports (>110 tiles).
                // Safely recycles slots 0..109 without corrupting TMapDialog member variables [this+0x3F4/0x3F8].
                s_pStubMem = VirtualAlloc(IntPtr.Zero, (UIntPtr)64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                s_pStubCounter = IntPtr.Add(s_pStubMem, 0x30);
                Marshal.WriteInt32(s_pStubCounter, 0);

                int stubAddr = s_pStubMem.ToInt32();
                int counterAddr = stubAddr + 0x30;
                int returnAddr = 0x0055D3EE;

                byte[] stub = new byte[52];
                // 0x00: cmp dx, 0x6e
                stub[0] = 0x66; stub[1] = 0x83; stub[2] = 0xFA; stub[3] = 0x6E;
                // 0x04: jl .ok (+25 bytes from 0x06 to 0x1F)
                stub[4] = 0x7C; stub[5] = 0x19;
                // 0x06: push ecx
                stub[6] = 0x51;
                // 0x07: mov edx, [counterAddr]
                stub[7] = 0x8B; stub[8] = 0x15;
                Buffer.BlockCopy(BitConverter.GetBytes(counterAddr), 0, stub, 9, 4);
                // 0x0D: inc dword ptr [counterAddr]
                stub[13] = 0xFF; stub[14] = 0x05;
                Buffer.BlockCopy(BitConverter.GetBytes(counterAddr), 0, stub, 15, 4);
                // 0x13: mov eax, edx
                stub[19] = 0x89; stub[20] = 0xD0;
                // 0x15: xor edx, edx
                stub[21] = 0x31; stub[22] = 0xD2;
                // 0x17: mov ecx, 110
                stub[23] = 0xB9; stub[24] = 0x6E; stub[25] = 0x00; stub[26] = 0x00; stub[27] = 0x00;
                // 0x1C: div ecx
                stub[28] = 0xF7; stub[29] = 0xF1;
                // 0x1E: pop ecx
                stub[30] = 0x59;
                // 0x1F: .ok: movsx eax, dx
                stub[31] = 0x0F; stub[32] = 0xBF; stub[33] = 0xC2;
                // 0x22: mov bx, word ptr [esi + eax*8 + 0x8a]
                stub[34] = 0x66; stub[35] = 0x8B; stub[36] = 0x9C; stub[37] = 0xC6;
                stub[38] = 0x8A; stub[39] = 0x00; stub[40] = 0x00; stub[41] = 0x00;
                // 0x2A: jmp returnAddr (from stubAddr + 0x2A + 5)
                int relRet = returnAddr - (stubAddr + 0x2A + 5);
                stub[42] = 0xE9;
                Buffer.BlockCopy(BitConverter.GetBytes(relRet), 0, stub, 43, 4);
                stub[47] = 0x90; // pad

                Marshal.Copy(stub, 0, s_pStubMem, stub.Length);

                // Patch ADDR_TMAPDIALOG_TILE_ALLOC (0x0055D3E3): 11 bytes (jmp to stub + 6 NOPs)
                VirtualProtect((IntPtr)ADDR_TMAPDIALOG_TILE_ALLOC, (UIntPtr)11, PAGE_EXECUTE_READWRITE, out oldProtect);
                byte[] tileAllocPatch = new byte[11];
                tileAllocPatch[0] = 0xE9;
                int relStub = stubAddr - (ADDR_TMAPDIALOG_TILE_ALLOC + 5);
                Buffer.BlockCopy(BitConverter.GetBytes(relStub), 0, tileAllocPatch, 1, 4);
                for (int i = 5; i < 11; i++) tileAllocPatch[i] = 0x90;
                Marshal.Copy(tileAllocPatch, 0, (IntPtr)ADDR_TMAPDIALOG_TILE_ALLOC, 11);
                VirtualProtect((IntPtr)ADDR_TMAPDIALOG_TILE_ALLOC, (UIntPtr)11, oldProtect, out oldProtect);

                // 4. Hook CopyBits presentation call at 0x0055D889
                s_origCopyBits = (MapCopyBitsDelegate)Marshal.GetDelegateForFunctionPointer((IntPtr)ADDR_ORIG_COPYBITS, typeof(MapCopyBitsDelegate));
                VirtualProtect((IntPtr)CALL_TMAPDIALOG_COPYBITS, (UIntPtr)5, PAGE_EXECUTE_READWRITE, out oldProtect);
                IntPtr myCopyBitsPtr = Marshal.GetFunctionPointerForDelegate(s_myMapCopyBits);
                int relOffset = myCopyBitsPtr.ToInt32() - (CALL_TMAPDIALOG_COPYBITS + 5);
                byte[] callHook = new byte[5];
                callHook[0] = 0xE8; // CALL rel32
                Buffer.BlockCopy(BitConverter.GetBytes(relOffset), 0, callHook, 1, 4);
                Marshal.Copy(callHook, 0, (IntPtr)CALL_TMAPDIALOG_COPYBITS, 5);
                VirtualProtect((IntPtr)CALL_TMAPDIALOG_COPYBITS, (UIntPtr)5, oldProtect, out oldProtect);

                // 5. Hook DrawString thunk entry point at 0x0040A63C:
                // Intercepts ALL tactical map labels: land cities, provinces, capitals, ports, and sea names
                s_origDrawString = (MapDrawStringDelegate)Marshal.GetDelegateForFunctionPointer((IntPtr)ADDR_DRAWSTRING_IMPL, typeof(MapDrawStringDelegate));
                VirtualProtect((IntPtr)ADDR_ORIG_DRAWSTRING, (UIntPtr)5, PAGE_EXECUTE_READWRITE, out oldProtect);
                IntPtr myDrawStringPtr = Marshal.GetFunctionPointerForDelegate(s_myMapDrawString);
                int relDrawString = myDrawStringPtr.ToInt32() - (ADDR_ORIG_DRAWSTRING + 5);
                byte[] drawStringHook = new byte[5];
                drawStringHook[0] = 0xE9; // JMP rel32
                Buffer.BlockCopy(BitConverter.GetBytes(relDrawString), 0, drawStringHook, 1, 4);
                Marshal.Copy(drawStringHook, 0, (IntPtr)ADDR_ORIG_DRAWSTRING, 5);
                VirtualProtect((IntPtr)ADDR_ORIG_DRAWSTRING, (UIntPtr)5, oldProtect, out oldProtect);

                // 6. Hook Slot 125 (DrawSelectionBox) in vtables
                int[] selBoxVtables = new int[] { VTABLE_TMAPDIALOG_DRAW_SELECTION_BOX, VTABLE_TNEWMAPDIALOG_DRAW_SELECTION_BOX, VTABLE_TMAPDIALOG3_DRAW_SELECTION_BOX };
                for (int i = 0; i < selBoxVtables.Length; i++)
                {
                    VirtualProtect((IntPtr)selBoxVtables[i], (UIntPtr)4, PAGE_EXECUTE_READWRITE, out oldProtect);
                    IntPtr origSelBoxPtr = Marshal.ReadIntPtr((IntPtr)selBoxVtables[i]);
                    if (s_origMapDrawSelectionBox == null && origSelBoxPtr != IntPtr.Zero)
                    {
                        s_origMapDrawSelectionBox = (MapDrawSelectionBoxDelegate)Marshal.GetDelegateForFunctionPointer(origSelBoxPtr, typeof(MapDrawSelectionBoxDelegate));
                    }
                    Marshal.WriteIntPtr((IntPtr)selBoxVtables[i], Marshal.GetFunctionPointerForDelegate(s_myMapDrawSelectionBox));
                    VirtualProtect((IntPtr)selBoxVtables[i], (UIntPtr)4, oldProtect, out oldProtect);
                }

                // 7. Hook 4 Selection CopyBits call sites
                int[] selCopyBitsCalls = new int[] {
                    CALL_TMAPDIALOG_SELECTION_COPYBITS1,
                    CALL_TMAPDIALOG_SELECTION_COPYBITS2,
                    CALL_TNEWMAPDIALOG_SELECTION_COPYBITS1,
                    CALL_TNEWMAPDIALOG_SELECTION_COPYBITS2
                };
                IntPtr mySelCopyBitsPtr = Marshal.GetFunctionPointerForDelegate(s_myMapSelectionCopyBits);
                for (int i = 0; i < selCopyBitsCalls.Length; i++)
                {
                    int callSite = selCopyBitsCalls[i];
                    VirtualProtect((IntPtr)callSite, (UIntPtr)5, PAGE_EXECUTE_READWRITE, out oldProtect);
                    int rel = mySelCopyBitsPtr.ToInt32() - (callSite + 5);
                    byte[] patch = new byte[5];
                    patch[0] = 0xE8; // CALL rel32
                    Buffer.BlockCopy(BitConverter.GetBytes(rel), 0, patch, 1, 4);
                    Marshal.Copy(patch, 0, (IntPtr)callSite, 5);
                    VirtualProtect((IntPtr)callSite, (UIntPtr)5, oldProtect, out oldProtect);
                }

                Log("Option B tactical map zoom-out engine hooks installed successfully (Buffer=2464x2304, TMapDialog::Draw @Slot69, SafeTileAlloc @0x0055D3E3, CopyBits @0x0055D889, DrawString @0x0040A63C, SelectionCopyBits x4, DrawSelectionBox @Slot125).");
            }
            catch (Exception ex)
            {
                Log("Option B engine hooks installation error: {0}", ex);
            }
        }

        private static int MyStretchDIBitsHook(
            IntPtr hdc, int xDest, int yDest, int DestWidth, int DestHeight,
            int xSrc, int ySrc, int SrcWidth, int SrcHeight,
            IntPtr lpBits, IntPtr lpbmi, uint iUsage, uint rop)
        {
            IntPtr hWnd = WindowFromDC(hdc);
            if (hWnd != IntPtr.Zero)
            {
                McWindowInfo mc;
                if (s_mcWindows.TryGetValue(hWnd, out mc))
                {
                    int dstX = (int)Math.Round((double)xDest * mc.PhysW / mc.OrigW);
                    int dstY = (int)Math.Round((double)yDest * mc.PhysH / mc.OrigH);
                    int dstRight = (int)Math.Round((double)(xDest + DestWidth) * mc.PhysW / mc.OrigW);
                    int dstBottom = (int)Math.Round((double)(yDest + DestHeight) * mc.PhysH / mc.OrigH);
                    int dstW = Math.Max(0, dstRight - dstX);
                    int dstH = Math.Max(0, dstBottom - dstY);

                    Log("AmbitMcWindow StretchDIBits: hWnd=0x{0:X}, src=({1},{2},{3}x{4}) -> dst=({5},{6},{7}x{8})",
                        hWnd.ToInt32(), xSrc, ySrc, SrcWidth, SrcHeight, dstX, dstY, dstW, dstH);

                    SetStretchBltMode(hdc, s_filter);
                    SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
                    return s_origStretchDIBits(hdc, dstX, dstY, dstW, dstH, xSrc, ySrc, SrcWidth, SrcHeight, lpBits, lpbmi, iUsage, rop);
                }

                if (hWnd == s_viewHwnd || hWnd == s_gameHwnd)
                {
                    EnsurePresentationConfigured();

                if (TargetW > 0 && TargetH > 0)
                {
                    int srcCanvasW = (SrcWidth > 0 && SrcWidth <= 800) ? SrcWidth : 800;
                    int srcCanvasH = (SrcHeight > 0 && SrcHeight <= 600) ? SrcHeight : 600;

                    int dstX;
                    int dstY;
                    int dstW;
                    int dstH;

                    bool isFullFrame = (DestWidth >= 790 && DestHeight >= 590);
                    if (isFullFrame)
                    {
                        dstX = DstX;
                        dstY = DstY;
                        dstW = TargetW;
                        dstH = TargetH;
                    }
                    else if (xDest >= DstX && (xDest + DestWidth) <= (DstX + TargetW + 16) && (xDest > 800 || yDest > 600))
                    {
                        dstX = xDest;
                        dstY = yDest;
                        dstW = DestWidth;
                        dstH = DestHeight;
                    }
                    else
                    {
                        dstX = DstX + (int)Math.Round((double)xDest * TargetW / 800.0);
                        dstY = DstY + (int)Math.Round((double)yDest * TargetH / 600.0);
                        int dstRight = DstX + (int)Math.Round((double)(xDest + DestWidth) * TargetW / 800.0);
                        int dstBottom = DstY + (int)Math.Round((double)(yDest + DestHeight) * TargetH / 600.0);
                        dstW = Math.Max(0, dstRight - dstX);
                        dstH = Math.Max(0, dstBottom - dstY);
                    }

                    // Clamp blit destination within presentation bounds to protect pillarboxes
                    if (dstX < DstX)
                    {
                        int cut = DstX - dstX;
                        dstX = DstX;
                        dstW = Math.Max(0, dstW - cut);
                    }
                    if (dstY < DstY)
                    {
                        int cut = DstY - dstY;
                        dstY = DstY;
                        dstH = Math.Max(0, dstH - cut);
                    }
                    if (dstX + dstW > DstX + TargetW)
                    {
                        dstW = Math.Max(0, DstX + TargetW - dstX);
                    }
                    if (dstY + dstH > DstY + TargetH)
                    {
                        dstH = Math.Max(0, DstY + TargetH - dstY);
                    }
                    if (dstW <= 0 || dstH <= 0) return 0;

                    SetStretchBltMode(hdc, s_filter);
                    SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
                    return s_origStretchDIBits(hdc, dstX, dstY, dstW, dstH, xSrc, ySrc, SrcWidth, SrcHeight, lpBits, lpbmi, iUsage, rop);
                }
            }
        }

        return s_origStretchDIBits(hdc, xDest, yDest, DestWidth, DestHeight, xSrc, ySrc, SrcWidth, SrcHeight, lpBits, lpbmi, iUsage, rop);
    }

        private static bool MyGetClientRectHook(IntPtr hWnd, out RECT lpRect)
        {
            bool res = s_origGetClientRect(hWnd, out lpRect);
            if (res && hWnd != IntPtr.Zero)
            {
                McWindowInfo mc;
                if (s_mcWindows.TryGetValue(hWnd, out mc))
                {
                    lpRect.Left = 0;
                    lpRect.Top = 0;
                    lpRect.Right = mc.OrigW;
                    lpRect.Bottom = mc.OrigH;
                    return res;
                }

                if (hWnd == s_viewHwnd)
                {
                    lpRect.Left = 0;
                    lpRect.Top = 0;
                    lpRect.Right = 800;
                    lpRect.Bottom = 600;
                }
            }
            return res;
        }

        private static bool MyInvalidateRectHook(IntPtr hWnd, IntPtr lpRect, bool bErase)
        {
            if (hWnd != IntPtr.Zero)
            {
                McWindowInfo mc;
                if (s_mcWindows.TryGetValue(hWnd, out mc))
                {
                    if (lpRect != IntPtr.Zero)
                    {
                        RECT rc = (RECT)Marshal.PtrToStructure(lpRect, typeof(RECT));
                        int rLeft = Math.Max(0, Math.Min(mc.OrigW, rc.Left));
                        int rTop = Math.Max(0, Math.Min(mc.OrigH, rc.Top));
                        int rRight = Math.Max(0, Math.Min(mc.OrigW, rc.Right));
                        int rBottom = Math.Max(0, Math.Min(mc.OrigH, rc.Bottom));

                        RECT scaled;
                        scaled.Left = (int)Math.Floor((double)rLeft * mc.PhysW / mc.OrigW);
                        scaled.Top = (int)Math.Floor((double)rTop * mc.PhysH / mc.OrigH);
                        scaled.Right = (int)Math.Ceiling((double)rRight * mc.PhysW / mc.OrigW);
                        scaled.Bottom = (int)Math.Ceiling((double)rBottom * mc.PhysH / mc.OrigH);

                        IntPtr pScaled = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RECT)));
                        try
                        {
                            Marshal.StructureToPtr(scaled, pScaled, false);
                            return s_origInvalidateRect(hWnd, pScaled, bErase);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pScaled);
                        }
                    }
                    return s_origInvalidateRect(hWnd, IntPtr.Zero, bErase);
                }

                if ((hWnd == s_viewHwnd || hWnd == s_gameHwnd) && TargetW > 0 && TargetH > 0)
                {
                    if (lpRect != IntPtr.Zero)
                    {
                        RECT rc = (RECT)Marshal.PtrToStructure(lpRect, typeof(RECT));
                        int rLeft = Math.Max(0, Math.Min(800, rc.Left));
                        int rTop = Math.Max(0, Math.Min(600, rc.Top));
                        int rRight = Math.Max(0, Math.Min(800, rc.Right));
                        int rBottom = Math.Max(0, Math.Min(600, rc.Bottom));

                        RECT scaled;
                        scaled.Left = DstX + (int)Math.Floor((double)rLeft * TargetW / 800.0);
                        scaled.Top = DstY + (int)Math.Floor((double)rTop * TargetH / 600.0);
                        scaled.Right = DstX + (int)Math.Ceiling((double)rRight * TargetW / 800.0);
                        scaled.Bottom = DstY + (int)Math.Ceiling((double)rBottom * TargetH / 600.0);

                        IntPtr pScaled = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RECT)));
                        try
                        {
                            Marshal.StructureToPtr(scaled, pScaled, false);
                            return s_origInvalidateRect(hWnd, pScaled, bErase);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pScaled);
                        }
                    }
                    else
                    {
                        return s_origInvalidateRect(hWnd, IntPtr.Zero, bErase);
                    }
                }
            }
            return s_origInvalidateRect(hWnd, lpRect, bErase);
        }

        private static bool MyValidateRectHook(IntPtr hWnd, IntPtr lpRect)
        {
            if (hWnd != IntPtr.Zero)
            {
                McWindowInfo mc;
                if (s_mcWindows.TryGetValue(hWnd, out mc))
                {
                    if (lpRect != IntPtr.Zero)
                    {
                        RECT rc = (RECT)Marshal.PtrToStructure(lpRect, typeof(RECT));
                        int rLeft = Math.Max(0, Math.Min(mc.OrigW, rc.Left));
                        int rTop = Math.Max(0, Math.Min(mc.OrigH, rc.Top));
                        int rRight = Math.Max(0, Math.Min(mc.OrigW, rc.Right));
                        int rBottom = Math.Max(0, Math.Min(mc.OrigH, rc.Bottom));

                        RECT scaled;
                        scaled.Left = (int)Math.Floor((double)rLeft * mc.PhysW / mc.OrigW);
                        scaled.Top = (int)Math.Floor((double)rTop * mc.PhysH / mc.OrigH);
                        scaled.Right = (int)Math.Ceiling((double)rRight * mc.PhysW / mc.OrigW);
                        scaled.Bottom = (int)Math.Ceiling((double)rBottom * mc.PhysH / mc.OrigH);

                        IntPtr pScaled = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RECT)));
                        try
                        {
                            Marshal.StructureToPtr(scaled, pScaled, false);
                            return s_origValidateRect(hWnd, pScaled);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pScaled);
                        }
                    }
                    return s_origValidateRect(hWnd, IntPtr.Zero);
                }

                if ((hWnd == s_viewHwnd || hWnd == s_gameHwnd) && TargetW > 0 && TargetH > 0)
                {
                    if (lpRect != IntPtr.Zero)
                    {
                        RECT rc = (RECT)Marshal.PtrToStructure(lpRect, typeof(RECT));
                        int rLeft = Math.Max(0, Math.Min(800, rc.Left));
                        int rTop = Math.Max(0, Math.Min(600, rc.Top));
                        int rRight = Math.Max(0, Math.Min(800, rc.Right));
                        int rBottom = Math.Max(0, Math.Min(600, rc.Bottom));

                        RECT scaled;
                        scaled.Left = DstX + (int)Math.Floor((double)rLeft * TargetW / 800.0);
                        scaled.Top = DstY + (int)Math.Floor((double)rTop * TargetH / 600.0);
                        scaled.Right = DstX + (int)Math.Ceiling((double)rRight * TargetW / 800.0);
                        scaled.Bottom = DstY + (int)Math.Ceiling((double)rBottom * TargetH / 600.0);

                        IntPtr pScaled = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RECT)));
                        try
                        {
                            Marshal.StructureToPtr(scaled, pScaled, false);
                            return s_origValidateRect(hWnd, pScaled);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pScaled);
                        }
                    }
                    else
                    {
                        return s_origValidateRect(hWnd, IntPtr.Zero);
                    }
                }
            }
            return s_origValidateRect(hWnd, lpRect);
        }

        private static IntPtr MyBeginPaintHook(IntPtr hWnd, out PAINTSTRUCT lpPaint)
        {
            IntPtr hdc = s_origBeginPaint(hWnd, out lpPaint);
            if (hdc != IntPtr.Zero && hWnd != IntPtr.Zero)
            {
                McWindowInfo mc;
                if (s_mcWindows.TryGetValue(hWnd, out mc))
                {
                    int vx1 = (int)Math.Max(0, Math.Floor((double)lpPaint.rcPaint.Left * mc.OrigW / mc.PhysW));
                    int vy1 = (int)Math.Max(0, Math.Floor((double)lpPaint.rcPaint.Top * mc.OrigH / mc.PhysH));
                    int vx2 = (int)Math.Min(mc.OrigW, Math.Ceiling((double)lpPaint.rcPaint.Right * mc.OrigW / mc.PhysW));
                    int vy2 = (int)Math.Min(mc.OrigH, Math.Ceiling((double)lpPaint.rcPaint.Bottom * mc.OrigH / mc.PhysH));
                    if (vx2 < vx1) vx2 = vx1;
                    if (vy2 < vy1) vy2 = vy1;

                    lpPaint.rcPaint.Left = vx1;
                    lpPaint.rcPaint.Top = vy1;
                    lpPaint.rcPaint.Right = vx2;
                    lpPaint.rcPaint.Bottom = vy2;
                    return hdc;
                }

                if ((hWnd == s_viewHwnd || hWnd == s_gameHwnd) && TargetW > 0 && TargetH > 0)
                {
                    // Un-scale physical 4K coordinates back to virtual 800x600 coordinate space with conservative bounds
                    int vx1 = (int)Math.Max(0, Math.Floor((double)(lpPaint.rcPaint.Left - DstX) * 800.0 / TargetW));
                    int vy1 = (int)Math.Max(0, Math.Floor((double)(lpPaint.rcPaint.Top - DstY) * 600.0 / TargetH));
                    int vx2 = (int)Math.Min(800, Math.Ceiling((double)(lpPaint.rcPaint.Right - DstX) * 800.0 / TargetW));
                    int vy2 = (int)Math.Min(600, Math.Ceiling((double)(lpPaint.rcPaint.Bottom - DstY) * 600.0 / TargetH));
                    if (vx2 < vx1) vx2 = vx1;
                    if (vy2 < vy1) vy2 = vy1;

                    lpPaint.rcPaint.Left = vx1;
                    lpPaint.rcPaint.Top = vy1;
                    lpPaint.rcPaint.Right = vx2;
                    lpPaint.rcPaint.Bottom = vy2;
                }
            }
            return hdc;
        }

        private static int MyGetClipBoxHook(IntPtr hdc, out RECT lprect)
        {
            int res = s_origGetClipBox(hdc, out lprect);
            IntPtr hWnd = WindowFromDC(hdc);
            if (res != 0 && hWnd != IntPtr.Zero)
            {
                McWindowInfo mc;
                if (s_mcWindows.TryGetValue(hWnd, out mc))
                {
                    int vx1 = (int)Math.Max(0, Math.Floor((double)lprect.Left * mc.OrigW / mc.PhysW));
                    int vy1 = (int)Math.Max(0, Math.Floor((double)lprect.Top * mc.OrigH / mc.PhysH));
                    int vx2 = (int)Math.Min(mc.OrigW, Math.Ceiling((double)lprect.Right * mc.OrigW / mc.PhysW));
                    int vy2 = (int)Math.Min(mc.OrigH, Math.Ceiling((double)lprect.Bottom * mc.OrigH / mc.PhysH));
                    if (vx2 < vx1) vx2 = vx1;
                    if (vy2 < vy1) vy2 = vy1;

                    lprect.Left = vx1;
                    lprect.Top = vy1;
                    lprect.Right = vx2;
                    lprect.Bottom = vy2;
                    return res;
                }

                if ((hWnd == s_viewHwnd || hWnd == s_gameHwnd) && TargetW > 0 && TargetH > 0)
                {
                    // Un-scale physical 4K coordinates back to virtual 800x600 coordinate space with conservative bounds
                    int vx1 = (int)Math.Max(0, Math.Floor((double)(lprect.Left - DstX) * 800.0 / TargetW));
                    int vy1 = (int)Math.Max(0, Math.Floor((double)(lprect.Top - DstY) * 600.0 / TargetH));
                    int vx2 = (int)Math.Min(800, Math.Ceiling((double)(lprect.Right - DstX) * 800.0 / TargetW));
                    int vy2 = (int)Math.Min(600, Math.Ceiling((double)(lprect.Bottom - DstY) * 600.0 / TargetH));
                    if (vx2 < vx1) vx2 = vx1;
                    if (vy2 < vy1) vy2 = vy1;

                    lprect.Left = vx1;
                    lprect.Top = vy1;
                    lprect.Right = vx2;
                    lprect.Bottom = vy2;
                }
            }
            return res;
        }

        private static int MyIntersectClipRectHook(IntPtr hdc, int left, int top, int right, int bottom)
        {
            IntPtr hWnd = WindowFromDC(hdc);
            if ((hWnd == s_viewHwnd || hWnd == s_gameHwnd) && TargetW > 0 && TargetH > 0)
            {
                int pL = DstX + (int)Math.Floor((double)left * TargetW / 800.0);
                int pT = DstY + (int)Math.Floor((double)top * TargetH / 600.0);
                int pR = DstX + (int)Math.Ceiling((double)right * TargetW / 800.0);
                int pB = DstY + (int)Math.Ceiling((double)bottom * TargetH / 600.0);
                return s_origIntersectClipRect(hdc, pL, pT, pR, pB);
            }
            if (hWnd != IntPtr.Zero)
            {
                McWindowInfo mc;
                if (s_mcWindows.TryGetValue(hWnd, out mc))
                {
                    int pL = (int)Math.Floor((double)left * mc.PhysW / mc.OrigW);
                    int pT = (int)Math.Floor((double)top * mc.PhysH / mc.OrigH);
                    int pR = (int)Math.Ceiling((double)right * mc.PhysW / mc.OrigW);
                    int pB = (int)Math.Ceiling((double)bottom * mc.PhysH / mc.OrigH);
                    return s_origIntersectClipRect(hdc, pL, pT, pR, pB);
                }
            }
            return s_origIntersectClipRect(hdc, left, top, right, bottom);
        }

        private static int MyExcludeClipRectHook(IntPtr hdc, int left, int top, int right, int bottom)
        {
            IntPtr hWnd = WindowFromDC(hdc);
            if ((hWnd == s_viewHwnd || hWnd == s_gameHwnd) && TargetW > 0 && TargetH > 0)
            {
                int pL = DstX + (int)Math.Floor((double)left * TargetW / 800.0);
                int pT = DstY + (int)Math.Floor((double)top * TargetH / 600.0);
                int pR = DstX + (int)Math.Ceiling((double)right * TargetW / 800.0);
                int pB = DstY + (int)Math.Ceiling((double)bottom * TargetH / 600.0);
                return s_origExcludeClipRect(hdc, pL, pT, pR, pB);
            }
            if (hWnd != IntPtr.Zero)
            {
                McWindowInfo mc;
                if (s_mcWindows.TryGetValue(hWnd, out mc))
                {
                    int pL = (int)Math.Floor((double)left * mc.PhysW / mc.OrigW);
                    int pT = (int)Math.Floor((double)top * mc.PhysH / mc.OrigH);
                    int pR = (int)Math.Ceiling((double)right * mc.PhysW / mc.OrigW);
                    int pB = (int)Math.Ceiling((double)bottom * mc.PhysH / mc.OrigH);
                    return s_origExcludeClipRect(hdc, pL, pT, pR, pB);
                }
            }
            return s_origExcludeClipRect(hdc, left, top, right, bottom);
        }

        private static bool MyScreenToClientHook(IntPtr hWnd, ref POINT lpPoint)
        {
            bool res = s_origScreenToClient(hWnd, ref lpPoint);
            if (res && hWnd != IntPtr.Zero)
            {
                McWindowInfo mc;
                if (s_mcWindows.TryGetValue(hWnd, out mc))
                {
                    int gx = (int)Math.Round((double)lpPoint.X * mc.OrigW / mc.PhysW);
                    int gy = (int)Math.Round((double)lpPoint.Y * mc.OrigH / mc.PhysH);
                    if (gx < 0) gx = 0;
                    if (gx > mc.OrigW - 1) gx = mc.OrigW - 1;
                    if (gy < 0) gy = 0;
                    if (gy > mc.OrigH - 1) gy = mc.OrigH - 1;

                    lpPoint.X = gx;
                    lpPoint.Y = gy;
                    return res;
                }

                if ((hWnd == s_viewHwnd || hWnd == s_gameHwnd) && TargetW > 0 && TargetH > 0)
                {
                    int gx = (int)Math.Round((double)(lpPoint.X - DstX) * 800.0 / TargetW);
                    int gy = (int)Math.Round((double)(lpPoint.Y - DstY) * 600.0 / TargetH);
                    if (gx < 0) gx = 0;
                    if (gx > 799) gx = 799;
                    if (gy < 0) gy = 0;
                    if (gy > 599) gy = 599;

                    if (s_zoomEnabled && s_zoomLevel != 1.0f && gx >= 8 && gx <= 586 && gy >= 58 && gy <= 570)
                    {
                        int mx, my;
                        MapZoomCoordinate(gx, gy, out mx, out my);
                        gx = mx;
                        gy = my;
                    }

                    lpPoint.X = gx;
                    lpPoint.Y = gy;
                }
            }
            return res;
        }

        private static bool MyMoveToExHook(IntPtr hdc, int X, int Y, IntPtr lpPoint)
        {
            IntPtr hWnd = WindowFromDC(hdc);
            if ((hWnd == s_viewHwnd || hWnd == s_gameHwnd) && TargetW > 0 && TargetH > 0)
            {
                int physX = DstX + (int)Math.Round((double)X * TargetW / 800.0);
                int physY = DstY + (int)Math.Round((double)Y * TargetH / 600.0);
                bool res = s_origMoveToEx(hdc, physX, physY, lpPoint);
                if (res && lpPoint != IntPtr.Zero)
                {
                    POINT pt = (POINT)Marshal.PtrToStructure(lpPoint, typeof(POINT));
                    pt.X = (int)Math.Round((double)(pt.X - DstX) * 800.0 / TargetW);
                    pt.Y = (int)Math.Round((double)(pt.Y - DstY) * 600.0 / TargetH);
                    Marshal.StructureToPtr(pt, lpPoint, false);
                }
                return res;
            }
            return s_origMoveToEx(hdc, X, Y, lpPoint);
        }

        private static bool MyLineToHook(IntPtr hdc, int nXEnd, int nYEnd)
        {
            IntPtr hWnd = WindowFromDC(hdc);
            if ((hWnd == s_viewHwnd || hWnd == s_gameHwnd) && TargetW > 0 && TargetH > 0)
            {
                int physX = DstX + (int)Math.Round((double)nXEnd * TargetW / 800.0);
                int physY = DstY + (int)Math.Round((double)nYEnd * TargetH / 600.0);
                return s_origLineTo(hdc, physX, physY);
            }
            return s_origLineTo(hdc, nXEnd, nYEnd);
        }

        private static bool MyPolylineHook(IntPtr hdc, IntPtr lppt, int cPoints)
        {
            return s_origPolyline(hdc, lppt, cPoints);
        }

        private static bool MyRectangleHook(IntPtr hdc, int nLeftRect, int nTopRect, int nRightRect, int nBottomRect)
        {
            IntPtr hWnd = WindowFromDC(hdc);
            if ((hWnd == s_viewHwnd || hWnd == s_gameHwnd) && TargetW > 0 && TargetH > 0)
            {
                int pL = DstX + (int)Math.Round((double)nLeftRect * TargetW / 800.0);
                int pT = DstY + (int)Math.Round((double)nTopRect * TargetH / 600.0);
                int pR = DstX + (int)Math.Round((double)nRightRect * TargetW / 800.0);
                int pB = DstY + (int)Math.Round((double)nBottomRect * TargetH / 600.0);
                return s_origRectangle(hdc, pL, pT, pR, pB);
            }
            return s_origRectangle(hdc, nLeftRect, nTopRect, nRightRect, nBottomRect);
        }

        private static int MySelectClipRgnHook(IntPtr hdc, IntPtr hrgn)
        {
            return s_origSelectClipRgn(hdc, hrgn);
        }

        private static int MyExtSelectClipRgnHook(IntPtr hdc, IntPtr hrgn, int fnMode)
        {
            return s_origExtSelectClipRgn(hdc, hrgn, fnMode);
        }

        private static bool MyScrollWindowHook(IntPtr hWnd, int XAmount, int YAmount, IntPtr lpRect, IntPtr lpClipRect)
        {
            return s_origScrollWindow(hWnd, XAmount, YAmount, lpRect, lpClipRect);
        }

        private static int MyScrollWindowExHook(IntPtr hWnd, int dx, int dy, IntPtr prcScroll, IntPtr prcClip, IntPtr hrgnUpdate, IntPtr prcUpdate, uint flags)
        {
            return s_origScrollWindowEx(hWnd, dx, dy, prcScroll, prcClip, hrgnUpdate, prcUpdate, flags);
        }

        private static int MyFillRectHook(IntPtr hDC, ref RECT lprc, IntPtr hbr)
        {
            return s_origFillRect(hDC, ref lprc, hbr);
        }

        private static int MyFrameRectHook(IntPtr hDC, ref RECT lprc, IntPtr hbr)
        {
            return s_origFrameRect(hDC, ref lprc, hbr);
        }
        private static IntPtr McWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            McWindowInfo mc;
            s_mcWindows.TryGetValue(hWnd, out mc);

            switch (uMsg)
            {
                case 0x0200: // WM_MOUSEMOVE
                case 0x0201: // WM_LBUTTONDOWN
                case 0x0202: // WM_LBUTTONUP
                case 0x0203: // WM_LBUTTONDBLCLK
                case 0x0204: // WM_RBUTTONDOWN
                case 0x0205: // WM_RBUTTONUP
                case 0x0206: // WM_RBUTTONDBLCLK
                case 0x0207: // WM_MBUTTONDOWN
                case 0x0208: // WM_MBUTTONUP
                    if (mc != null && mc.PhysW > 0 && mc.PhysH > 0)
                    {
                        int clientX = unchecked((short)(lParam.ToInt32() & 0xFFFF));
                        int clientY = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));

                        int gameX = (int)Math.Round((double)clientX * mc.OrigW / mc.PhysW);
                        int gameY = (int)Math.Round((double)clientY * mc.OrigH / mc.PhysH);

                        if (gameX < 0) gameX = 0;
                        if (gameX > mc.OrigW - 1) gameX = mc.OrigW - 1;
                        if (gameY < 0) gameY = 0;
                        if (gameY > mc.OrigH - 1) gameY = mc.OrigH - 1;

                        lParam = (IntPtr)((gameY << 16) | (gameX & 0xFFFF));
                    }
                    break;

                case 0x0014: // WM_ERASEBKGND
                    if (mc != null && mc.PhysW > 0 && mc.PhysH > 0)
                    {
                        IntPtr blackBrush = GetStockObject(BLACK_BRUSH);
                        RECT rc = new RECT { Left = 0, Top = 0, Right = mc.PhysW, Bottom = mc.PhysH };
                        FillRect(wParam, ref rc, blackBrush);
                    }
                    return (IntPtr)1;

                case 0x0082: // WM_NCDESTROY
                    if (mc != null)
                    {
                        IntPtr orig = mc.OrigWndProc;
                        s_mcWindows.Remove(hWnd);
                        Log("AmbitMcWindow 0x{0:X} destroyed, remaining active: {1}", hWnd.ToInt32(), s_mcWindows.Count);

                        if (s_gameHwnd != IntPtr.Zero)
                        {
                            EnableWindow(s_gameHwnd, true);
                        }
                        if (s_viewHwnd != IntPtr.Zero)
                        {
                            EnableWindow(s_viewHwnd, true);
                            if (s_mcWindows.Count == 0)
                            {
                                SetActiveWindow(s_viewHwnd);
                                SetFocus(s_viewHwnd);
                                InvalidateRect(s_viewHwnd, IntPtr.Zero, false);
                            }
                        }
                        if (orig != IntPtr.Zero)
                        {
                            return CallWindowProcA(orig, hWnd, uMsg, wParam, lParam);
                        }
                        return IntPtr.Zero;
                    }
                    break;
            }

            if (mc != null && mc.OrigWndProc != IntPtr.Zero)
            {
                return CallWindowProcA(mc.OrigWndProc, hWnd, uMsg, wParam, lParam);
            }
            return DefWindowProcA(hWnd, uMsg, wParam, lParam);
        }

        private static IntPtr ViewWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            switch (uMsg)
            {
                case 0x0200: // WM_MOUSEMOVE
                case 0x0201: // WM_LBUTTONDOWN
                case 0x0202: // WM_LBUTTONUP
                case 0x0203: // WM_LBUTTONDBLCLK
                case 0x0204: // WM_RBUTTONDOWN
                case 0x0205: // WM_RBUTTONUP
                case 0x0206: // WM_RBUTTONDBLCLK
                case 0x0208: // WM_MBUTTONUP
                    {
                        if (s_mcWindows.Count > 0)
                        {
                            if (uMsg >= 0x0201 && uMsg <= 0x0209)
                            {
                                return IntPtr.Zero;
                            }
                        }

                        if (TargetW > 0 && TargetH > 0)
                        {
                            int clientX = unchecked((short)(lParam.ToInt32() & 0xFFFF));
                            int clientY = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));

                            int gameX = (int)Math.Round((double)(clientX - DstX) * 800.0 / TargetW);
                            int gameY = (int)Math.Round((double)(clientY - DstY) * 600.0 / TargetH);

                            if (gameX < 0) gameX = 0;
                            if (gameX > 799) gameX = 799;
                            if (gameY < 0) gameY = 0;
                            if (gameY > 599) gameY = 599;

                            if (s_zoomEnabled && s_zoomLevel != 1.0f && gameX >= 8 && gameX <= 586 && gameY >= 58 && gameY <= 570)
                            {
                                int mx, my;
                                MapZoomCoordinate(gameX, gameY, out mx, out my);
                                gameX = mx;
                                gameY = my;
                            }

                            lParam = (IntPtr)((gameY << 16) | (gameX & 0xFFFF));
                        }
                    }
                    break;

                case 0x0207: // WM_MBUTTONDOWN - Reset zoom
                    {
                        if (s_zoomEnabled)
                        {
                            if (s_zoomLevel != 1.0f)
                            {
                                SetZoom(1.0f);
                                Log("World map zoom reset to 1.0x via middle-click");
                                TriggerMapRedraw(hWnd);
                                return IntPtr.Zero;
                            }
                        }
                    }
                    break;

                case 0x020A: // WM_MOUSEWHEEL
                    {
                        if (s_zoomEnabled && TargetW > 0 && TargetH > 0)
                        {
                            short delta = unchecked((short)((wParam.ToInt32() >> 16) & 0xFFFF));
                            POINT pt;
                            pt.X = unchecked((short)(lParam.ToInt32() & 0xFFFF));
                            pt.Y = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));
                            ScreenToClient(hWnd, ref pt);

                            // Convert physical client coordinates to virtual game canvas coordinates
                            int gameX = (int)Math.Round((double)(pt.X - DstX) * 800.0 / TargetW);
                            int gameY = (int)Math.Round((double)(pt.Y - DstY) * 600.0 / TargetH);

                            // Check if cursor is over the world map viewport (X=8..586, Y=58..570)
                            if (gameX >= 8 && gameX <= 586 && gameY >= 58 && gameY <= 570)
                            {
                                if (delta < 0) // Zoom out (wheel down)
                                {
                                    if (s_zoomLevel > 0.25f)
                                    {
                                        float newZoom;
                                        if (s_zoomLevel > 1.0f)
                                        {
                                            newZoom = (float)Math.Round(Math.Max(1.0f, s_zoomLevel - 0.25f), 2);
                                        }
                                        else
                                        {
                                            // Option B tactical zoom-out levels: 0.75x, 0.50x, 0.25x
                                            newZoom = (float)Math.Round(Math.Max(0.25f, s_zoomLevel - 0.25f), 2);
                                        }

                                        if (newZoom != s_zoomLevel)
                                        {
                                            SetZoom(newZoom, gameX, gameY);
                                            Log("World map zoom out: {0}x (wheel down)", s_zoomLevel);
                                            TriggerMapRedraw(hWnd);
                                            return IntPtr.Zero;
                                        }
                                    }
                                }
                                else if (delta > 0) // Zoom in (wheel up)
                                {
                                    if (s_zoomLevel < 3.0f)
                                    {
                                        float newZoom;
                                        int focalX = gameX;
                                        int focalY = gameY;

                                        if (s_zoomLevel < 1.0f)
                                        {
                                            // Option B tactical zoom-in levels: 0.25x -> 0.50x -> 0.75x -> 1.0f
                                            newZoom = (float)Math.Round(Math.Min(1.0f, s_zoomLevel + 0.25f), 2);
                                        }
                                        else
                                        {
                                            // Magnification levels: 1.0x -> 1.25x -> 1.50x -> ... up to 3.0x
                                            newZoom = (float)Math.Round(Math.Min(3.0f, s_zoomLevel + 0.25f), 2);
                                            if (s_zoomLevel > 1.0f)
                                            {
                                                double u = (gameX - 8) / 578.0;
                                                double v = (gameY - 58) / 512.0;
                                                focalX = (int)Math.Round(s_zoomEffectiveSx + u * s_zoomEffectiveSrcW);
                                                focalY = (int)Math.Round(s_zoomEffectiveSy + v * s_zoomEffectiveSrcH);
                                            }
                                        }

                                        if (newZoom != s_zoomLevel)
                                        {
                                            SetZoom(newZoom, focalX, focalY);
                                            Log("World map zoom in: {0}x at focal ({1},{2})", s_zoomLevel, s_zoomFocalX, s_zoomFocalY);
                                            TriggerMapRedraw(hWnd);
                                            return IntPtr.Zero;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    break;

                case 0x0046: // WM_WINDOWPOSCHANGING
                    {
                        if (WindowW > 0 && WindowH > 0 && lParam != IntPtr.Zero)
                        {
                            WINDOWPOS wp = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                            if (wp.x != 0 || wp.y != 0 || wp.cx != WindowW || wp.cy != WindowH)
                            {
                                Log("ViewWndProc WM_WINDOWPOSCHANGING: correcting pos=({0},{1}), size=({2}x{3}) -> (0,0,{4}x{5})",
                                    wp.x, wp.y, wp.cx, wp.cy, WindowW, WindowH);
                                wp.x = 0;
                                wp.y = 0;
                                wp.cx = WindowW;
                                wp.cy = WindowH;
                                wp.flags &= ~SWP_NOMOVE;
                                wp.flags &= ~SWP_NOSIZE;
                                Marshal.StructureToPtr(wp, lParam, false);
                                return IntPtr.Zero;
                            }
                        }
                    }
                    break;

                case 0x0014: // WM_ERASEBKGND
                    {
                        Log("ViewWndProc WM_ERASEBKGND: hDC=0x{0:X}, DstX={1}, DstY={2}, Target={3}x{4}",
                            wParam.ToInt32(), DstX, DstY, TargetW, TargetH);
                        if (DstX > 0)
                        {
                            IntPtr blackBrush = GetStockObject(BLACK_BRUSH);
                            RECT leftPillar = new RECT { Left = 0, Top = 0, Right = DstX, Bottom = WindowH };
                            RECT rightPillar = new RECT { Left = DstX + TargetW, Top = 0, Right = WindowW, Bottom = WindowH };
                            FillRect(wParam, ref leftPillar, blackBrush);
                            FillRect(wParam, ref rightPillar, blackBrush);
                        }
                        if (DstY > 0)
                        {
                            IntPtr blackBrush = GetStockObject(BLACK_BRUSH);
                            RECT topBar = new RECT { Left = 0, Top = 0, Right = WindowW, Bottom = DstY };
                            RECT botBar = new RECT { Left = 0, Top = DstY + TargetH, Right = WindowW, Bottom = WindowH };
                            FillRect(wParam, ref topBar, blackBrush);
                            FillRect(wParam, ref botBar, blackBrush);
                        }
                        return (IntPtr)1;
                    }

                case 0x0100: // WM_KEYDOWN
                    {
                        int vk = wParam.ToInt32();
                        if (vk == 0x7A) // VK_F11: Cycle zoom filter (Nearest -> Sharp -> Smooth)
                        {
                            s_zoomFilter = (ZoomFilter)(((int)s_zoomFilter + 1) % 3);
                            Log("World map zoom filter changed to: {0} (via F11)", s_zoomFilter);
                            TriggerMapRedraw(hWnd);
                            return IntPtr.Zero;
                        }
                    }
                    break;
            }

            return CallWindowProcA(s_origViewWndProc, hWnd, uMsg, wParam, lParam);
        }

        private static IntPtr GameWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            switch (uMsg)
            {
                case 0x020A: // WM_MOUSEWHEEL
                case 0x0207: // WM_MBUTTONDOWN
                    if (s_viewHwnd != IntPtr.Zero)
                    {
                        return ViewWndProc(s_viewHwnd, uMsg, wParam, lParam);
                    }
                    break;

                case 0x0014: // WM_ERASEBKGND
                    return (IntPtr)1;

                case 0x0046: // WM_WINDOWPOSCHANGING
                    {
                        if (s_mode == DisplayMode.Fullscreen && lParam != IntPtr.Zero)
                        {
                            int screenW = Screen.PrimaryScreen.Bounds.Width;
                            int screenH = Screen.PrimaryScreen.Bounds.Height;
                            WINDOWPOS wp = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                            if (wp.x != 0 || wp.y != 0 || wp.cx != screenW || wp.cy != screenH)
                            {
                                Log("GameWndProc WM_WINDOWPOSCHANGING: correcting pos=({0},{1}), size=({2}x{3}) -> (0,0,{4}x{5})",
                                    wp.x, wp.y, wp.cx, wp.cy, screenW, screenH);
                                wp.x = 0;
                                wp.y = 0;
                                wp.cx = screenW;
                                wp.cy = screenH;
                                wp.flags &= ~SWP_NOMOVE;
                                wp.flags &= ~SWP_NOSIZE;
                                Marshal.StructureToPtr(wp, lParam, false);
                                return IntPtr.Zero;
                            }
                        }
                    }
                    break;

                case 0x0005: // WM_SIZE
                    {
                        int newW = unchecked((short)(lParam.ToInt32() & 0xFFFF));
                        int newH = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));
                        if (newW > 0 && newH > 0)
                        {
                            RecalculateGeometry(newW, newH);
                            if (s_viewHwnd != IntPtr.Zero)
                            {
                                SetWindowPos(s_viewHwnd, IntPtr.Zero, 0, 0, newW, newH, SWP_NOZORDER | SWP_SHOWWINDOW);
                            }
                        }
                    }
                    break;

                case 0x0100: // WM_KEYDOWN
                    {
                        int vk = wParam.ToInt32();
                        if (vk == 0x0D && ((lParam.ToInt32() & (1 << 29)) != 0)) // Alt+Enter
                        {
                            ToggleDisplayMode(hWnd);
                            return IntPtr.Zero;
                        }
                        if (vk == 0x7A) // VK_F11: Cycle zoom filter (Nearest -> Sharp -> Smooth)
                        {
                            s_zoomFilter = (ZoomFilter)(((int)s_zoomFilter + 1) % 3);
                            Log("World map zoom filter changed to: {0} (via F11)", s_zoomFilter);
                            TriggerMapRedraw(s_viewHwnd != IntPtr.Zero ? s_viewHwnd : hWnd);
                            return IntPtr.Zero;
                        }
                    }
                    break;
            }

            return CallWindowProcA(s_origGameWndProc, hWnd, uMsg, wParam, lParam);
        }

        private static void ToggleDisplayMode(IntPtr hWnd)
        {
            int screenW = Screen.PrimaryScreen.Bounds.Width;
            int screenH = Screen.PrimaryScreen.Bounds.Height;

            if (s_mode == DisplayMode.Fullscreen)
            {
                s_mode = DisplayMode.Windowed;
                int winH = Math.Min(screenH - 100, 1200);
                int winW = winH * 4 / 3;
                int x = (screenW - winW) / 2;
                int y = (screenH - winH) / 2;

                SetWindowLongA(hWnd, GWL_STYLE, (IntPtr)unchecked((int)(WS_OVERLAPPEDWINDOW | WS_VISIBLE)));
                SetWindowPos(hWnd, IntPtr.Zero, x, y, winW, winH, SWP_NOZORDER | SWP_FRAMECHANGED);
                RecalculateGeometry(winW, winH);
                if (s_viewHwnd != IntPtr.Zero)
                {
                    SetWindowPos(s_viewHwnd, IntPtr.Zero, 0, 0, winW, winH, SWP_NOZORDER | SWP_SHOWWINDOW);
                }
                Log("Toggled to Windowed mode: {0}x{1}", winW, winH);
            }
            else
            {
                s_mode = DisplayMode.Fullscreen;
                SetWindowLongA(hWnd, GWL_STYLE, (IntPtr)unchecked((int)(WS_POPUP | WS_VISIBLE)));
                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, screenW, screenH, SWP_NOZORDER | SWP_FRAMECHANGED);
                RecalculateGeometry(screenW, screenH);
                if (s_viewHwnd != IntPtr.Zero)
                {
                    SetWindowPos(s_viewHwnd, IntPtr.Zero, 0, 0, screenW, screenH, SWP_NOZORDER | SWP_SHOWWINDOW);
                }
                Log("Toggled to Fullscreen mode: {0}x{1}", screenW, screenH);
            }
            InvalidateRect(hWnd, IntPtr.Zero, true);
            if (s_viewHwnd != IntPtr.Zero)
            {
                InvalidateRect(s_viewHwnd, IntPtr.Zero, true);
            }
        }
    }
}
