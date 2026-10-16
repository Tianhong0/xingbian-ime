// 形变输入法 —— 端到端验证 harness：ITextStoreACP 互操作层
//
// ============================ 方法序的权威依据 ============================
// ITextStoreACP **不在** msctf.h 里，它在 textstor.h / textstor.idl。
// 本机可下载到的原版（Microsoft IDL 内容，由 mingw-w64 / Wine / ReactOS 原样分发）：
//   scratch\e2e\textstor.h   (31982 B)  https://cdn.jsdelivr.net/gh/mingw-w64/mingw-w64@master/mingw-w64-headers/include/textstor.h
//   scratch\e2e\textstor.idl (10663 B)  https://cdn.jsdelivr.net/gh/reactos/reactos@master/sdk/include/psdk/textstor.idl
//
// 证据一（vtable 结构体，textstor.h:508-699 `typedef struct ITextStoreACPVtbl { ... }`）
//   三个 IUnknown 槽位之后，ITextStoreACP 方法按此顺序出现：
//     0  AdviseSink                      13 InsertTextAtSelection
//     1  UnadviseSink                    14 InsertEmbeddedAtSelection
//     2  RequestLock                     15 RequestSupportedAttrs
//     3  GetStatus                       16 RequestAttrsAtPosition
//     4  QueryInsert                     17 RequestAttrsTransitioningAtPosition
//     5  GetSelection                    18 FindNextAttrTransition
//     6  SetSelection                    19 RetrieveRequestedAttrs
//     7  GetText                         20 GetEndACP
//     8  SetText                         21 GetActiveView
//     9  GetFormattedText                22 GetACPFromPoint
//    10  GetEmbedded                     23 GetTextExt
//    11  QueryInsertEmbedded             24 GetScreenExt
//    12  InsertEmbedded                  25 GetWnd
//   合计 26 个方法（+ 3 个 IUnknown 槽位 = vtable 共 29 槽）。
//
// 证据二（IDL 声明顺序，textstor.idl:152-302 `interface ITextStoreACP : IUnknown`）
//   与上面的 vtable 顺序逐条一致（IDL 方法声明顺序 === vtable 顺序）。
//
// 证据三（IID，textstor.h:350 `DEFINE_GUID(IID_ITextStoreACP, 0x28888fe3, 0xc2a0, 0x483a, 0xa3,0xea, 0x8c,0xb1,0xce,0x51,0xff,0x3d)`）
//   即 28888fe3-c2a0-483a-a3ea-8cb1ce51ff3d。
//
// ⚠️ [ComImport] + InterfaceIsIUnknown 的托管接口：CLR 隐式放入 3 个 IUnknown 槽位，
//    之后按托管声明顺序排布。因此下面的声明顺序必须与上表逐字对应。
// ========================================================================

using System;
using System.Runtime.InteropServices;

namespace Xingbian.E2E
{
    // =====================================================================================
    // 常量与结构体（原值取自 textstor.h / textstor.idl）
    // =====================================================================================

    public static class Ts
    {
        // textstor.h:45-56
        public const int TS_E_INVALIDPOS = unchecked((int)0x80040200);
        public const int TS_E_NOLOCK = unchecked((int)0x80040201);
        public const int TS_E_NOOBJECT = unchecked((int)0x80040202);
        public const int TS_E_NOSERVICE = unchecked((int)0x80040203);
        public const int TS_E_NOINTERFACE = unchecked((int)0x80040204);
        public const int TS_E_NOSELECTION = unchecked((int)0x80040205);
        public const int TS_E_NOLAYOUT = unchecked((int)0x80040206);
        public const int TS_E_INVALIDPOINT = unchecked((int)0x80040207);
        public const int TS_E_SYNCHRONOUS = unchecked((int)0x80040208);
        public const int TS_E_READONLY = unchecked((int)0x80040209);
        public const int TS_E_FORMAT = unchecked((int)0x8004020a);
        /// <summary>textstor.h:56 —— MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_ITF, 0x0300) = 0x00040300。</summary>
        public const int TS_S_ASYNC = unchecked((int)0x00040300);

        public const uint TS_DEFAULT_SELECTION = 0xFFFFFFFFu;

        // textstor.h:105-109
        public const uint TS_LF_SYNC = 0x1;
        public const uint TS_LF_READ = 0x2;
        public const uint TS_LF_READWRITE = 0x6;

        // textstor.h:117-119
        public const uint TS_IAS_NOQUERY = 0x1;
        public const uint TS_IAS_QUERYONLY = 0x2;

        // textstor.idl:67-68
        public const int TS_AE_NONE = 0;
        public const int TS_AE_START = 1;
        public const int TS_AE_END = 2;
        public const int TS_RT_PLAIN = 0;
        public const int TS_RT_HIDDEN = 1;
        public const int TS_RT_OPAQUE = 2;

        // textstor.h:93-103 —— sink 掩码
        public const uint TS_AS_TEXT_CHANGE = 0x01;
        public const uint TS_AS_SEL_CHANGE = 0x02;
        public const uint TS_AS_LAYOUT_CHANGE = 0x04;
        public const uint TS_AS_ATTR_CHANGE = 0x08;
        public const uint TS_AS_STATUS_CHANGE = 0x10;
    }

    /// <summary>textstor.idl:71-75 —— TS_STATUS { DWORD dwDynamicFlags; DWORD dwStaticFlags; }</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TS_STATUS
    {
        public uint dwDynamicFlags;
        public uint dwStaticFlags;
    }

    /// <summary>textstor.idl:77-82 —— TS_TEXTCHANGE { LONG acpStart; LONG acpOldEnd; LONG acpNewEnd; }</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TS_TEXTCHANGE
    {
        public int acpStart;
        public int acpOldEnd;
        public int acpNewEnd;
    }

    /// <summary>textstor.idl:84-88 —— TS_SELECTIONSTYLE { TsActiveSelEnd ase; BOOL fInterimChar; }</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TS_SELECTIONSTYLE
    {
        public int ase;
        public int fInterimChar;   // BOOL, 4 bytes
    }

    /// <summary>textstor.idl:90-95 —— TS_SELECTION_ACP { LONG acpStart; LONG acpEnd; TS_SELECTIONSTYLE style; } (16 B on x64)</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TS_SELECTION_ACP
    {
        public int acpStart;
        public int acpEnd;
        public TS_SELECTIONSTYLE style;
    }

    /// <summary>textstor.idl:97-101 —— TS_RUNINFO { ULONG uCount; TsRunType type; }</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TS_RUNINFO
    {
        public uint uCount;
        public int type;
    }

    // =====================================================================================
    // ITextStoreACPSink —— msctf.dll 侧实现，我们（文本存储）调用它的 OnLockGranted
    // =====================================================================================

    /// <summary>
    /// ITextStoreACPSink（textstor.idl:118-145，IID 22d44c94-a419-4542-a272-ae26093ececf）。
    /// Vtbl（相对 IUnknown）：0 OnTextChange / 1 OnSelectionChange / 2 OnLayoutChange /
    ///   3 OnStatusChange / 4 OnAttrsChange / 5 OnLockGranted /
    ///   6 OnStartEditTransaction / 7 OnEndEditTransaction
    /// ★ 关键：文本存储在 RequestLock 里**必须**回调 OnLockGranted（同步授权时），
    ///   否则 msctf 不会去调 ITfEditSession::DoEditSession。
    /// </summary>
    [ComImport]
    [Guid("22d44c94-a419-4542-a272-ae26093ececf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITextStoreACPSink
    {
        // 槽位 0
        [PreserveSig]
        int OnTextChange(uint dwFlags, ref TS_TEXTCHANGE pChange);
        // 槽位 1
        [PreserveSig]
        int OnSelectionChange();
        // 槽位 2
        [PreserveSig]
        int OnLayoutChange(int lcode, uint vcView);
        // 槽位 3
        [PreserveSig]
        int OnStatusChange(uint dwFlags);
        // 槽位 4
        [PreserveSig]
        int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs);
        // 槽位 5 ★★★ 唯一的编辑会话入口 ★★★
        [PreserveSig]
        int OnLockGranted(uint dwLockFlags);
        // 槽位 6
        [PreserveSig]
        int OnStartEditTransaction();
        // 槽位 7
        [PreserveSig]
        int OnEndEditTransaction();
    }

    // =====================================================================================
    // ITextStoreACP —— 我们要实现的假文本存储
    // =====================================================================================

    /// <summary>
    /// ITextStoreACP（textstor.idl:147-302 / textstor.h:508-699）。
    /// IID 28888fe3-c2a0-483a-a3ea-8cb1ce51ff3d。26 个方法，顺序与 vtable 逐条对应。
    /// </summary>
    [ComImport]
    [Guid("28888fe3-c2a0-483a-a3ea-8cb1ce51ff3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITextStoreACP
    {
        // 槽位 0
        [PreserveSig]
        int AdviseSink(ref Guid riid, IntPtr punk, uint dwMask);
        // 槽位 1
        [PreserveSig]
        int UnadviseSink(IntPtr punk);
        // 槽位 2
        [PreserveSig]
        int RequestLock(uint dwLockFlags, out int phrSession);
        // 槽位 3
        [PreserveSig]
        int GetStatus(ref TS_STATUS pdcs);
        // 槽位 4
        [PreserveSig]
        int QueryInsert(int acpTestStart, int acpTestEnd, uint cch, out int pacpResultStart, out int pacpResultEnd);
        // 槽位 5
        [PreserveSig]
        int GetSelection(uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched);
        // 槽位 6
        [PreserveSig]
        int SetSelection(uint ulCount, IntPtr pSelection);
        // 槽位 7
        [PreserveSig]
        int GetText(int acpStart, int acpEnd, IntPtr pchPlain, uint cchPlainReq, out uint pcchPlainRet,
                    IntPtr prgRunInfo, uint cRunInfoReq, out uint pcRunInfoRet, out int pacpNext);
        // 槽位 8
        [PreserveSig]
        int SetText(uint dwFlags, int acpStart, int acpEnd, IntPtr pchText, uint cch, ref TS_TEXTCHANGE pChange);
        // 槽位 9
        [PreserveSig]
        int GetFormattedText(int acpStart, int acpEnd, out IntPtr ppDataObject);
        // 槽位 10
        [PreserveSig]
        int GetEmbedded(int acpPos, ref Guid rguidService, ref Guid riid, out IntPtr ppunk);
        // 槽位 11
        [PreserveSig]
        int QueryInsertEmbedded(ref Guid pguidService, IntPtr pFormatEtc, out int pfInsertable);
        // 槽位 12
        [PreserveSig]
        int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, IntPtr pDataObject, ref TS_TEXTCHANGE pChange);
        // 槽位 13 ★★★ 提交路径的见证点 ★★★
        [PreserveSig]
        int InsertTextAtSelection(uint dwFlags, IntPtr pchText, uint cch,
                                  out int pacpStart, out int pacpEnd, ref TS_TEXTCHANGE pChange);
        // 槽位 14
        [PreserveSig]
        int InsertEmbeddedAtSelection(uint dwFlags, IntPtr pDataObject,
                                      out int pacpStart, out int pacpEnd, ref TS_TEXTCHANGE pChange);
        // 槽位 15
        [PreserveSig]
        int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, IntPtr paFilterAttrs);
        // 槽位 16
        [PreserveSig]
        int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags);
        // 槽位 17
        [PreserveSig]
        int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags);
        // 槽位 18
        [PreserveSig]
        int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs, IntPtr paFilterAttrs,
                                   uint dwFlags, out int pacpNext, out int pfFound, out int plFoundOffset);
        // 槽位 19
        [PreserveSig]
        int RetrieveRequestedAttrs(uint ulCount, IntPtr paAttrVals, out uint pcFetched);
        // 槽位 20
        [PreserveSig]
        int GetEndACP(out int pacp);
        // 槽位 21
        [PreserveSig]
        int GetActiveView(out uint pvcView);
        // 槽位 22
        [PreserveSig]
        int GetACPFromPoint(uint vcView, IntPtr ptScreen, uint dwFlags, out int pacp);
        // 槽位 23
        [PreserveSig]
        int GetTextExt(uint vcView, int acpStart, int acpEnd, out Xingbian.Tsf.Interop.RECT prc, out int pfClipped);
        // 槽位 24
        [PreserveSig]
        int GetScreenExt(uint vcView, out Xingbian.Tsf.Interop.RECT prc);
        // 槽位 25
        [PreserveSig]
        int GetWnd(uint vcView, out IntPtr phwnd);
    }

    // =====================================================================================
    // harness 自己的原生导入
    // =====================================================================================

    internal static class E2ENative
    {
        /// <summary>
        /// 取本线程的真实 TSF 线程管理器。**不需要注册任何 TIP**。
        /// TsfInterop 里的对应声明是 internal 的，harness 引用不到，所以这里重新声明。
        /// </summary>
        [DllImport("msctf.dll", PreserveSig = false)]
        internal static extern void TF_GetThreadMgr(
            [MarshalAs(UnmanagedType.Interface)] out Xingbian.Tsf.Interop.ITfThreadMgr ppdim);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        internal static extern IntPtr CreateWindowExW(uint dwExStyle,
            [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
            [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr FindWindowW([MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
                                                  [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out Xingbian.Tsf.Interop.RECT lpRect);

        // ---- 消息泵 ----
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PeekMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,
                                                 uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DispatchMessageW")]
        internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "TranslateMessage")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetKeyboardState(byte[] lpKeyState);

        // ---- 截图 ----
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
                                           IntPtr hdcSrc, int xSrc, int ySrc, uint dwRop);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr hdc);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    }

    /// <summary>Win32 MSG。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    /// <summary>ITextStoreACP 的 IID（textstor.h:350）。</summary>
    public static class TextStoreIids
    {
        public static readonly Guid IID_ITextStoreACP = new Guid("28888fe3-c2a0-483a-a3ea-8cb1ce51ff3d");
        public static readonly Guid IID_ITextStoreACPSink = new Guid("22d44c94-a419-4542-a272-ae26093ececf");
    }
}
