// ============================================================================
//  CandidatePanel.Native.cs
//  —— CandidatePanel 的【纯 Win32 / GDI 互操作层】
//
//  职责边界（与 CandidatePanel.cs 的分工）：
//    本文件只做"翻译"：P/Invoke 声明 + 原生结构体 + Win32 常量 + 最小工具函数。
//    不含任何布局 / 绘制 / 生命周期 / 业务逻辑 —— 那些全在 CandidatePanel.cs。
//
//  全项目互操作约定：
//    * 每个 DllImport 都显式写 SetLastError = true + CharSet = CharSet.Unicode，
//      并显式指定 EntryPoint = "...W"，保证永远调用宽字符版本，不受平台默认影响。
//    * 字符串参数一律 [MarshalAs(UnmanagedType.LPWStr)] string。
//    * 互操作结构体字段沿用 Win32 原始小写命名（left / tmHeight / ...），
//      便于与 MSDN 逐字段对照核对偏移。
//    * 目标平台 x64（Directory.Build.props 锁定 PlatformTarget=x64），
//      故窗口 user-data 直接使用 SetWindowLongPtrW / GetWindowLongPtrW。
//
//  禁止：System.Windows.Forms / System.Drawing / WPF。本文件只用 mscorlib。
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Text; // StringBuilder，仅用于 GetTextFaceW 读回真实字体名

namespace Xingbian.Tsf
{
    /// <summary>
    /// CandidatePanel 使用的全部 Win32 / GDI 互操作符号。纯静态，无状态。
    /// </summary>
    internal static class PanelNative
    {
        // ====================================================================
        //  Win32 常量
        // ====================================================================

        // ---- 窗口样式（CreateWindowExW）------------------------------------
        /// <summary>WS_POPUP = 0x80000000，弹出式窗口（无标题栏 / 无系统边框装饰）。</summary>
        internal const uint WS_POPUP = 0x80000000u;
        /// <summary>WS_EX_TOOLWINDOW：不出现在 Alt+Tab / 任务栏。</summary>
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        /// <summary>WS_EX_TOPMOST：永远浮在非置顶窗口之上。</summary>
        internal const int WS_EX_TOPMOST = 0x00000008;
        /// <summary>WS_EX_NOACTIVATE：点击 / 显示都不激活本窗口，不抢焦点。</summary>
        internal const int WS_EX_NOACTIVATE = 0x08000000;

        // ---- ShowWindow ----------------------------------------------------
        internal const int SW_HIDE = 0;
        /// <summary>SW_SHOWNOACTIVATE = 4：显示但不激活（无焦点浮窗的关键）。</summary>
        internal const int SW_SHOWNOACTIVATE = 4;

        // ---- SetWindowPos 标志 ---------------------------------------------
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;

        /// <summary>窗口消息：系统主题色 / 配色方案发生变化（需要重建画刷并重绘）。</summary>
        internal const uint WM_SYSCOLORCHANGE = 0x0015;

        // ---- 窗口消息 ------------------------------------------------------
        internal const uint WM_PAINT = 0x000F;
        internal const uint WM_ERASEBKGND = 0x0014;
        internal const uint WM_MOUSEACTIVATE = 0x0021;
        internal const uint WM_NCCREATE = 0x0081;
        internal const uint WM_NCDESTROY = 0x0082;
        internal const uint WM_NCHITTEST = 0x0084;
        /// <summary>鼠标移动（lParam = 客户区坐标，低 16 位 X / 高 16 位 Y）。</summary>
        internal const uint WM_MOUSEMOVE = 0x0200;
        /// <summary>左键按下。面板会吞掉它，避免宿主同时响应。</summary>
        internal const uint WM_LBUTTONDOWN = 0x0201;
        /// <summary>左键抬起。命中测试 → 触发 ItemClicked。</summary>
        internal const uint WM_LBUTTONUP = 0x0202;
        /// <summary>鼠标离开客户区（需先 TrackMouseEvent(TME_LEAVE) 才会收到）。</summary>
        internal const uint WM_MOUSELEAVE = 0x02A3;
        /// <summary>
        /// 把客户区画到 wParam 指定的 DC。自绘窗口必须处理它，
        /// 否则 PrintWindow / 某些录屏与辅助工具抓到的是一片空白。
        /// </summary>
        internal const uint WM_PRINTCLIENT = 0x0318;

        // ---- 自定义消息（跨线程投递用；WM_APP = 0x8000）---------------------
        /// <summary>WM_APP + 1：在 UI 线程上应用一次 Show（wParam = GCHandle，指向 ShowRequest）。</summary>
        internal const uint WM_XB_SHOW = 0x8000 + 1;
        /// <summary>WM_APP + 2：在 UI 线程上执行 Hide。</summary>
        internal const uint WM_XB_HIDE = 0x8000 + 2;
        /// <summary>WM_APP + 3：在 UI 线程上销毁窗口并释放 GDI 对象。</summary>
        internal const uint WM_XB_DESTROY = 0x8000 + 3;

        /// <summary>WM_NCHITTEST 返回值：本窗口对鼠标完全透明，消息穿透到下层窗口。本面板不使用。</summary>
        internal const int HTTRANSPARENT = -1;
        /// <summary>WM_NCHITTEST 返回值：落在客户区，正常接收鼠标消息（本面板需要点击，故用这个）。</summary>
        internal const int HTCLIENT = 1;

        /// <summary>
        /// WM_MOUSEACTIVATE 返回值：不要激活本窗口（点击不会抢走宿主焦点）。
        /// ⚠ WinUser.h 实际值：MA_ACTIVATE=1 / MA_ACTIVATEANDEAT=2 / MA_NOACTIVATE=3 / MA_NOACTIVATEANDEAT=4。
        ///   本面板既要"不激活"又要收到点击，所以必须用 3；用 4 会把这次点击吃掉，ItemClicked 就永远不会触发。
        /// </summary>
        internal const int MA_NOACTIVATE = 3;
        /// <summary>仅为对照保留：不激活**并且**吃掉这次点击（本面板不可用，否则点选失效）。</summary>
        internal const int MA_NOACTIVATEANDEAT = 4;

        // ---- TrackMouseEvent ------------------------------------------------
        /// <summary>TME_LEAVE：鼠标离开客户区时发一条 WM_MOUSELEAVE。</summary>
        internal const uint TME_LEAVE = 0x00000002;

        // ---- GetSysColor 索引（系统主题色，跟随用户的高对比度设置）------------
        /// <summary>COLOR_WINDOW：普通行背景。</summary>
        internal const int COLOR_WINDOW = 5;
        /// <summary>COLOR_WINDOWTEXT：普通行文字。</summary>
        internal const int COLOR_WINDOWTEXT = 8;
        /// <summary>COLOR_HIGHLIGHT：选中行背景。</summary>
        internal const int COLOR_HIGHLIGHT = 13;
        /// <summary>COLOR_HIGHLIGHTTEXT：选中行文字。</summary>
        internal const int COLOR_HIGHLIGHTTEXT = 14;
        /// <summary>COLOR_3DSHADOW：1px 边框。</summary>
        internal const int COLOR_3DSHADOW = 16;
        /// <summary>COLOR_GRAYTEXT：次要说明文字（右侧注释 / 序号）。</summary>
        internal const int COLOR_GRAYTEXT = 17;

        /// <summary>IDC_ARROW = MAKEINTRESOURCE(32512)，作为 LoadCursorW 的 lpCursorName。</summary>
        internal static readonly IntPtr IDC_ARROW = new IntPtr(32512);

        /// <summary>窗口实例指针槽位（SetWindowLongPtrW / GetWindowLongPtrW 的 nIndex）。</summary>
        internal const int GWLP_USERDATA = -21;

        // ---- DrawTextW 格式位 ----------------------------------------------
        internal const uint DT_VCENTER = 0x00000004;
        internal const uint DT_SINGLELINE = 0x00000020;
        internal const uint DT_NOPREFIX = 0x00000800;
        internal const uint DT_END_ELLIPSIS = 0x00008000;
        /// <summary>DT_RIGHT：右对齐（序号列用它对齐小数点）。</summary>
        internal const uint DT_RIGHT = 0x00000002;

        // ---- BitBlt 光栅操作码 ----------------------------------------------
        internal const uint SRCCOPY = 0x00CC0020;

        // ---- GetDeviceCaps 索引 --------------------------------------------
        internal const int LOGPIXELSY = 90;

        // ---- SetBkMode ------------------------------------------------------
        internal const int TRANSPARENT = 1;

        // ---- MonitorFromPoint -----------------------------------------------
        internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        // ---- GetSystemMetrics ------------------------------------------------
        internal const int SM_XVIRTUALSCREEN = 76;
        internal const int SM_YVIRTUALSCREEN = 77;
        internal const int SM_CXVIRTUALSCREEN = 78;
        internal const int SM_CYVIRTUALSCREEN = 79;

        // ---- 错误码 ----------------------------------------------------------
        /// <summary>ERROR_CLASS_ALREADY_EXISTS：类名已注册（幂等注册时应当视为成功）。</summary>
        internal const int ERROR_CLASS_ALREADY_EXISTS = 1410;

        // ---- 字体 / 字符集 ---------------------------------------------------
        internal const uint DEFAULT_CHARSET = 1;
        internal const uint OUT_TT_PRECIS = 7;
        internal const uint CLIP_DEFAULT_PRECIS = 0;
        internal const uint CLEARTYPE_QUALITY = 5;
        internal const int FW_NORMAL = 400;
        /// <summary>FF_SWISS：无衬线族，供 GDI 字体映射器做兜底匹配。</summary>
        internal const uint FF_SWISS = 0x20;

        // ====================================================================
        //  原生结构体
        // ====================================================================

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        /// <summary>
        /// TEXTMETRICW。注意 tmFirstChar/tmLastChar/tmDefaultChar/tmBreakChar 是 WCHAR，
        /// 必须靠 CharSet.Unicode 才能按 2 字节封送。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct TEXTMETRICW
        {
            public int tmHeight;
            public int tmAscent;
            public int tmDescent;
            public int tmInternalLeading;
            public int tmExternalLeading;
            public int tmAveCharWidth;
            public int tmMaxCharWidth;
            public int tmWeight;
            public int tmOverhang;
            public int tmDigitizedAspectX;
            public int tmDigitizedAspectY;
            public char tmFirstChar;
            public char tmLastChar;
            public char tmDefaultChar;
            public char tmBreakChar;
            public byte tmItalic;
            public byte tmUnderlined;
            public byte tmStruckOut;
            public byte tmPitchAndFamily;
            public byte tmCharSet;
        }

        /// <summary>
        /// PAINTSTRUCT。rgbReserved[32] 用 8 个 int 展开表达（而不是 ByValArray byte[]）：
        /// 这样 out 参数不需要封送器分配托管数组，且 x64/x86 下布局都与原生一致。
        /// 我们只读 hdc，其余字段原样回传给 EndPaint。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PAINTSTRUCT
        {
            public IntPtr hdc;
            [MarshalAs(UnmanagedType.Bool)] public bool fErase;
            public RECT rcPaint;
            [MarshalAs(UnmanagedType.Bool)] public bool fRestore;
            [MarshalAs(UnmanagedType.Bool)] public bool fIncUpdate;
            // BYTE rgbReserved[32] —— 展开成 8 * 4 字节，保持总长度与对齐
            public int rsv0;
            public int rsv1;
            public int rsv2;
            public int rsv3;
            public int rsv4;
            public int rsv5;
            public int rsv6;
            public int rsv7;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct LOGFONTW
        {
            public int lfHeight;
            public int lfWidth;
            public int lfEscapement;
            public int lfOrientation;
            public int lfWeight;
            public byte lfItalic;
            public byte lfUnderline;
            public byte lfStrikeOut;
            public byte lfCharSet;
            public byte lfOutPrecision;
            public byte lfClipPrecision;
            public byte lfQuality;
            public byte lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string lfFaceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            /// <summary>必须为 NULL：背景由我们自己在 WM_PAINT 里擦，避免闪烁。</summary>
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        /// <summary>
        /// TRACKMOUSEEVENT。cbSize 必须等于 Marshal.SizeOf(TRACKMOUSEEVENT)，
        /// 否则 TrackMouseEvent 直接返回 FALSE 且不报错（很容易踩的静默失败）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct TRACKMOUSEEVENT
        {
            public uint cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        // ====================================================================
        //  回调委托
        // ====================================================================

        /// <summary>
        /// 窗口过程。必须是 Winapi(StdCall) 调用约定。
        /// ⚠ 调用方必须把这个委托的实例保存到【静态字段】里，
        ///   否则它会被 GC 回收，窗口消息打进来时直接访问已释放的跳板 → 宿主进程崩溃。
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // ====================================================================
        //  user32.dll
        // ====================================================================

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
        internal static extern ushort RegisterClassExW([In] ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterClassW([MarshalAs(UnmanagedType.LPWStr)] string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        internal static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
            [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
            uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
        internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "DestroyWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ShowWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "IsWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "IsWindowVisible")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetWindowPos")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetWindowRect")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetWindowLongPtrW")]
        internal static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetWindowLongPtrW")]
        internal static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetDC")]
        internal static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ReleaseDC")]
        internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "BeginPaint")]
        internal static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EndPaint")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EndPaint(IntPtr hWnd, [In] ref PAINTSTRUCT lpPaint);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetClientRect")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "InvalidateRect")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "UpdateWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateWindow(IntPtr hWnd);

        /// <summary>用画刷填充矩形（背景 / 高亮条 / 分隔线）。</summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FillRect")]
        internal static extern int FillRect(IntPtr hDC, [In] ref RECT lprc, IntPtr hbr);

        /// <summary>用画刷画 1px 边框（矩形四条边，画在矩形内侧）。</summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FrameRect")]
        internal static extern int FrameRect(IntPtr hDC, [In] ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")]
        internal static extern int DrawTextW(
            IntPtr hdc,
            [MarshalAs(UnmanagedType.LPWStr)] string lpchText,
            int cchText,
            [In, Out] ref RECT lprc,
            uint format);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "MonitorFromPoint")]
        internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfoW(IntPtr hMonitor, [In, Out] ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetSystemMetrics")]
        internal static extern int GetSystemMetrics(int nIndex);

        /// <summary>
        /// 取 (lprc) 所在 / 最近显示器的句柄。
        /// 面板锚点是插入符矩形，用 MonitorFromRect 比 MonitorFromPoint 更稳：
        /// 插入符贴着屏幕边缘时，点可能落到显示器外，而矩形仍与最近显示器相交。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "MonitorFromRect")]
        internal static extern IntPtr MonitorFromRect([In] ref RECT lprc, uint dwFlags);

        /// <summary>
        /// 取系统主题色（COLORREF）。返回值随用户的主题 / 高对比度设置变化，
        /// 所以永远在运行时读，不要把结果硬编码成常量。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetSysColor")]
        internal static extern uint GetSysColor(int nIndex);

        /// <summary>
        /// 请求鼠标离开客户区时收到 WM_MOUSELEAVE。必须每次进入客户区后重新调用
        /// （它是一次性注册，收到 WM_MOUSELEAVE 后自动失效）。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "TrackMouseEvent")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TrackMouseEvent([In, Out] ref TRACKMOUSEEVENT lpEventTrack);

        /// <summary>投递消息后立即返回（不等待处理）。跨线程把 Show/Hide 切回 UI 线程靠它。</summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>加载系统光标。lpCursorName 用 <see cref="IDC_ARROW"/> 这种 MAKEINTRESOURCE 常量。</summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LoadCursorW")]
        internal static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

        // ====================================================================
        //  gdi32.dll
        // ====================================================================

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")]
        internal static extern IntPtr CreateFontW(
            int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
            uint bItalic, uint bUnderline, uint bStrikeOut,
            uint iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
            [MarshalAs(UnmanagedType.LPWStr)] string pszFaceName);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateSolidBrush")]
        internal static extern IntPtr CreateSolidBrush(uint crColor);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateCompatibleDC")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateCompatibleBitmap")]
        internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "DeleteDC")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "BitBlt")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "DeleteObject")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SelectObject")]
        internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetTextColor")]
        internal static extern uint SetTextColor(IntPtr hdc, uint crColor);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetBkMode")]
        internal static extern int SetBkMode(IntPtr hdc, int mode);

        /// <summary>用 TextOutW 画文字（宽度确定放得下时的快路径）。</summary>
        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "TextOutW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TextOutW(IntPtr hdc, int x, int y, [MarshalAs(UnmanagedType.LPWStr)] string lpString, int c);

        /// <summary>测量字符串像素宽度。</summary>
        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTextExtentPoint32W(IntPtr hdc, [MarshalAs(UnmanagedType.LPWStr)] string lpString, int c, out SIZE psizl);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetTextMetricsW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW lptm);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetTextFaceW")]
        internal static extern int GetTextFaceW(IntPtr hdc, int nCount, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder lpName);

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetDeviceCaps")]
        internal static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        // ====================================================================
        //  kernel32.dll
        // ====================================================================

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        internal static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetProcAddress")]
        internal static extern IntPtr GetProcAddress(IntPtr hModule, [MarshalAs(UnmanagedType.LPStr)] string lpProcName);

        /// <summary>整数版 (nNumber * nNumerator) / nDenominator，四舍五入。</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "MulDiv")]
        internal static extern int MulDiv(int nNumber, int nNumerator, int nDenominator);

        // ====================================================================
        //  小工具
        // ====================================================================

        /// <summary>
        /// GetDpiForWindow 的托管签名（Win10 1607+ 才有）。
        /// 老系统上 user32.dll 里根本没有这个导出，所以不能直接 DllImport —— 那样会在
        /// JIT 该方法时抛 EntryPointNotFoundException。必须走 GetProcAddress 动态解析。
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate uint GetDpiForWindowDelegate(IntPtr hWnd);

        /// <summary>-1 = 尚未解析；IntPtr.Zero = 已确认本机没有该导出（不再重试）。</summary>
        private static IntPtr s_getDpiForWindowPtr = new IntPtr(-1);
        private static GetDpiForWindowDelegate s_getDpiForWindow;
        /// <summary>专用于保护上面的延迟解析；锁自建对象而不是 typeof(...)，避免与外部锁竞争。</summary>
        private static readonly object s_dpiResolveLock = new object();

        /// <summary>
        /// 尝试用 GetDpiForWindow 取窗口 DPI（Win10 1607+）。
        /// 本机没有该导出、调用失败或返回离谱值时返回 false，由调用方回退。
        /// </summary>
        internal static bool TryGetDpiForWindow(IntPtr hWnd, out int dpi)
        {
            dpi = 0;
            if (hWnd == IntPtr.Zero) return false;

            try
            {
                lock (s_dpiResolveLock)
                {
                    if (s_getDpiForWindowPtr == new IntPtr(-1))
                    {
                        IntPtr user32 = GetModuleHandleW("user32.dll");
                        s_getDpiForWindowPtr = user32 != IntPtr.Zero
                            ? GetProcAddress(user32, "GetDpiForWindow")
                            : IntPtr.Zero;

                        if (s_getDpiForWindowPtr != IntPtr.Zero)
                        {
                            try
                            {
                                s_getDpiForWindow = (GetDpiForWindowDelegate)
                                    Marshal.GetDelegateForFunctionPointer(
                                        s_getDpiForWindowPtr, typeof(GetDpiForWindowDelegate));
                            }
                            catch (Exception)
                            {
                                s_getDpiForWindow = null;
                                s_getDpiForWindowPtr = IntPtr.Zero;
                            }
                        }
                    }
                }

                if (s_getDpiForWindow == null) return false;

                uint v = s_getDpiForWindow(hWnd);
                // 合理区间外的值一概不用（0 / 异常大值都见过）
                if (v < 48u || v > 480u) return false;

                dpi = (int)v;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 组装 COLORREF（0x00BBGGRR）。注意字节序不是 RGB —— 直接写 0xRRGGBB 会串色。
        /// </summary>
        internal static uint Rgb(int r, int g, int b)
        {
            return (uint)((r & 0xFF) | ((g & 0xFF) << 8) | ((b & 0xFF) << 16));
        }

        /// <summary>
        /// 读回某个 HFONT 实际被 GDI 映射到的字体族名（可用它验证"微软雅黑 UI"是否真的存在）。
        /// 失败返回 null。所有异常就地吞掉。
        /// </summary>
        internal static string GetRealizedFaceName(IntPtr hFont)
        {
            if (hFont == IntPtr.Zero) return null;

            IntPtr hdc = IntPtr.Zero;
            IntPtr hOld = IntPtr.Zero;
            try
            {
                hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return null;

                hOld = SelectObject(hdc, hFont);

                StringBuilder sb = new StringBuilder(64);
                int n = GetTextFaceW(hdc, sb.Capacity, sb);
                if (n <= 0) return null;
                return sb.ToString();
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (hdc != IntPtr.Zero)
                {
                    if (hOld != IntPtr.Zero) SelectObject(hdc, hOld);
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }
        }
    }
}
