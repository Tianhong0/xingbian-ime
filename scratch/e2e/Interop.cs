// harness 自己的互操作层。
//
// 为什么不能复用 src\Xingbian.Tsf\Interop\TsfInterop.cs：
//   * ITextStoreACP / ITextStoreACPSink 是「文本存储侧」接口，TsfInterop.cs 里没有；
//   * TsfNative 是 internal，harness 引用不到，必须自己 DllImport。
//
// ITextStoreACP 的方法顺序取自 mingw-w64 的 textstor.h（= Windows SDK 的 textstor.idl），
// 见 scratch\e2e\textstor.h 第 508-699 行的 ITextStoreACPVtbl。

using System;
using System.Runtime.InteropServices;
using Xingbian.Tsf.Interop;   // 复用 RECT / ITfContext / ITfEditSession / ITfThreadMgr 等

namespace Xingbian.E2E
{
    // =====================================================================================
    // 常量
    // =====================================================================================

    public static class Ts
    {
        // ---- HRESULT ----
        public const int S_OK = 0;
        public const int E_NOTIMPL = unchecked((int)0x80004001);
        public const int E_NOINTERFACE = unchecked((int)0x80004002);
        public const int E_FAIL = unchecked((int)0x80004005);
        public const int E_INVALIDARG = unchecked((int)0x80070057);

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

        /// <summary>TS_S_ASYNC = MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_ITF, 0x0300) = 0x00040300。
        /// 注意它**不是** 1；TF_S_ASYNC 的值与它相同。</summary>
        public const int TS_S_ASYNC = 0x00040300;

        // ---- 锁标志 ----
        public const uint TS_LF_SYNC = 0x1;
        public const uint TS_LF_READ = 0x2;
        public const uint TS_LF_READWRITE = 0x6;

        // ---- 选区 ----
        public const uint TS_AE_NONE = 0;
        public const uint TS_AE_START = 1;
        public const uint TS_AE_END = 2;

        // ---- run 类型 ----
        public const uint TS_RT_PLAIN = 0;
        public const uint TS_RT_HIDDEN = 1;
        public const uint TS_RT_OPAQUE = 2;

        // ---- GetStatus 的动态/静态标志（我们全部报 0，既不只读也不需要 sink 通知）----
        public const uint TS_SD_READONLY = 0x1;
        public const uint TS_SD_LOADING = 0x2;
        public const uint TS_SD_UIINTEGRATION = 0x2000;
    }

    // =====================================================================================
    // 结构体
    // =====================================================================================

    [StructLayout(LayoutKind.Sequential)]
    public struct TS_STATUS
    {
        public uint dwDynamicFlags;
        public uint dwStaticFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TS_TEXTCHANGE
    {
        public int acpStart;
        public int acpOldEnd;
        public int acpNewEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TS_SELECTIONSTYLE
    {
        public uint ase;          // TsActiveSelEnd
        public int fInterimChar;  // BOOL
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TS_SELECTION_ACP
    {
        public int acpStart;
        public int acpEnd;
        public TS_SELECTIONSTYLE style;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TS_RUNINFO
    {
        public uint uCount;
        public uint type;         // TsRunType
    }

    // =====================================================================================
    // ITextStoreACPSink —— TSF 交给我们 AdviseSink 的回调对象
    //   IID 22d44c94-a419-4542-a272-ae26093ececf
    //   8 个方法：OnTextChange / OnSelectionChange / OnLayoutChange / OnStatusChange /
    //             OnAttrsChange / OnLockGranted / OnStartEditTransaction / OnEndEditTransaction
    // =====================================================================================

    [ComImport]
    [Guid("22d44c94-a419-4542-a272-ae26093ececf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITextStoreACPSink
    {
        [PreserveSig]
        int OnTextChange(uint dwFlags, ref TS_TEXTCHANGE pChange);
        [PreserveSig]
        int OnSelectionChange();
        [PreserveSig]
        int OnLayoutChange(uint lcode, uint vcView);
        [PreserveSig]
        int OnStatusChange(uint dwFlags);
        [PreserveSig]
        int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs);
        [PreserveSig]
        int OnLockGranted(uint dwLockFlags);
        [PreserveSig]
        int OnStartEditTransaction();
        [PreserveSig]
        int OnEndEditTransaction();
    }

    // =====================================================================================
    // ITextStoreACP
    //   IID 28888fe3-c2a0-483a-a3ea-8cb1ce51ff3d
    //   26 个方法，顺序 = textstor.h 的 ITextStoreACPVtbl 顺序（0..25）
    // =====================================================================================

    [ComImport]
    [Guid("28888fe3-c2a0-483a-a3ea-8cb1ce51ff3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITextStoreACP
    {
        // 0
        [PreserveSig]
        int AdviseSink(ref Guid riid, IntPtr punk, uint dwMask);
        // 1
        [PreserveSig]
        int UnadviseSink(IntPtr punk);
        // 2
        [PreserveSig]
        int RequestLock(uint dwLockFlags, out int phrSession);
        // 3
        [PreserveSig]
        int GetStatus(ref TS_STATUS pdcs);
        // 4
        [PreserveSig]
        int QueryInsert(int acpTestStart, int acpTestEnd, uint cch, out int pacpResultStart, out int pacpResultEnd);
        // 5
        [PreserveSig]
        int GetSelection(uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched);
        // 6
        [PreserveSig]
        int SetSelection(uint ulCount, IntPtr pSelection);
        // 7
        [PreserveSig]
        int GetText(int acpStart, int acpEnd, IntPtr pchPlain, uint cchPlainReq, out uint pcchPlainRet,
                    IntPtr prgRunInfo, uint cRunInfoReq, out uint pcRunInfoRet, out int pacpNext);
        // 8
        [PreserveSig]
        int SetText(uint dwFlags, int acpStart, int acpEnd, IntPtr pchText, uint cch, ref TS_TEXTCHANGE pChange);
        // 9
        [PreserveSig]
        int GetFormattedText(int acpStart, int acpEnd, out IntPtr ppDataObject);
        // 10
        [PreserveSig]
        int GetEmbedded(int acpPos, ref Guid rguidService, ref Guid riid, out IntPtr ppunk);
        // 11
        [PreserveSig]
        int QueryInsertEmbedded(ref Guid pguidService, IntPtr pFormatEtc, out int pfInsertable);
        // 12
        [PreserveSig]
        int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, IntPtr pDataObject, ref TS_TEXTCHANGE pChange);
        // 13
        [PreserveSig]
        int InsertTextAtSelection(uint dwFlags, IntPtr pchText, uint cch,
                                  out int pacpStart, out int pacpEnd, ref TS_TEXTCHANGE pChange);
        // 14
        [PreserveSig]
        int InsertEmbeddedAtSelection(uint dwFlags, IntPtr pDataObject,
                                      out int pacpStart, out int pacpEnd, ref TS_TEXTCHANGE pChange);
        // 15
        [PreserveSig]
        int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, IntPtr paFilterAttrs);
        // 16
        [PreserveSig]
        int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags);
        // 17
        [PreserveSig]
        int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags);
        // 18
        [PreserveSig]
        int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs, IntPtr paFilterAttrs, uint dwFlags,
                                   out int pacpNext, out int pfFound, out int plFoundOffset);
        // 19
        [PreserveSig]
        int RetrieveRequestedAttrs(uint ulCount, IntPtr paAttrVals, out uint pcFetched);
        // 20
        [PreserveSig]
        int GetEndACP(out int pacp);
        // 21
        [PreserveSig]
        int GetActiveView(out uint pvcView);
        // 22
        [PreserveSig]
        int GetACPFromPoint(uint vcView, IntPtr ptScreen, uint dwFlags, out int pacp);
        // 23
        [PreserveSig]
        int GetTextExt(uint vcView, int acpStart, int acpEnd, out RECT prc, out int pfClipped);
        // 24
        [PreserveSig]
        int GetScreenExt(uint vcView, out RECT prc);
        // 25
        [PreserveSig]
        int GetWnd(uint vcView, out IntPtr phwnd);
    }

    // =====================================================================================
    // msctf.dll
    // =====================================================================================

    public static class E2ENative
    {
        [DllImport("msctf.dll", CharSet = CharSet.Unicode)]
        public static extern int TF_GetThreadMgr([MarshalAs(UnmanagedType.Interface)] out ITfThreadMgr ppdim);

        [DllImport("msctf.dll", CharSet = CharSet.Unicode)]
        public static extern int TF_CreateThreadMgr([MarshalAs(UnmanagedType.Interface)] out ITfThreadMgr ppdim);

        // ---- user32 ----
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetDesktopWindow();

        /// <summary>把当前线程初始化为 STA。TSF 要求 UI 线程是 STA。</summary>
        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        public const uint COINIT_APARTMENTTHREADED = 0x2;
        public const uint COINIT_DISABLE_OLE1DDE = 0x4;
    }
}
