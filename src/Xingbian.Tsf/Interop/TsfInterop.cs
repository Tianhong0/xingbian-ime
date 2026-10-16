// 形变输入法 —— TSF (Text Services Framework) COM 互操作层
//
// 本文件的每一个 IID 与方法顺序都逐条核对过 Windows SDK 10.0.16299.0 的原版头文件
//   .ref\sdk_msctf.h   423667 B
//   .ref\sdk_msctf.idl  90038 B
// 源：https://cdn.jsdelivr.net/gh/tpn/winsdk-10@master/Include/10.0.16299.0/um/msctf.h
//
// 核对方法：解析头文件里的 `struct IXXXVtbl { ... }` 结构体，成员顺序即 vtable 顺序，
// 扣除前置的 QueryInterface/AddRef/Release 三个 IUnknown 槽位即得 COM 方法序。
// 全部 17 个接口已逐个打印比对（见 scratch/interop/sigs.txt）。
//
// ⚠️ 本文件里的“方法相对序号”注释是 vtable 布局的唯一依据。
//    任何新增/删除/重排方法都会静默破坏 COM 调用（表现为宿主崩溃，而不是编译错误）。
//
// 为什么手写而不是用 tlbimp/RegAsm 生成的互操作程序集：
//   本机没有 Windows SDK，也没有 .NET Framework targeting pack 之外的任何工具链，
//   只能手写 [ComImport] 接口。tsf 的接口都是 [local] 接口（无 proxy/stub），
//   但进程内加载时 CLR 的 CCW 直接按 vtable 槽位调用托管方法，不需要封送，所以完全可行。

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Xingbian.Tsf.Interop
{
    // =====================================================================================
    // 1. GUID 常量
    // =====================================================================================

    /// <summary>TSF 相关接口 IID 与 CLSID 常量。</summary>
    public static class TsfIids
    {
        // ---- 我们实现的接口（TIP 侧） ----
        /// <summary>ITfTextInputProcessor（Vtbl 槽位 3..4）。</summary>
        public static readonly Guid IID_ITfTextInputProcessor =
            new Guid("aa80e7f7-2021-11d2-93e0-0060b067b86e");
        /// <summary>ITfTextInputProcessorEx（Vtbl 槽位 5：ActivateEx）。继承 ITfTextInputProcessor。</summary>
        public static readonly Guid IID_ITfTextInputProcessorEx =
            new Guid("6e4e2102-f9cd-433d-b496-303ce03a6507");
        /// <summary>ITfKeyEventSink（Vtbl 槽位 3..8）。[local] 接口。</summary>
        public static readonly Guid IID_ITfKeyEventSink =
            new Guid("aa80e7f5-2021-11d2-93e0-0060b067b86e");
        /// <summary>ITfThreadMgrEventSink（Vtbl 槽位 3..7）。[local] 接口。</summary>
        public static readonly Guid IID_ITfThreadMgrEventSink =
            new Guid("aa80e80e-2021-11d2-93e0-0060b067b86e");
        /// <summary>ITfEditSession（Vtbl 槽位 3）。[local] 接口。</summary>
        public static readonly Guid IID_ITfEditSession =
            new Guid("aa80e803-2021-11d2-93e0-0060b067b86e");

        // ---- 我们调用的接口 ----
        /// <summary>ITfThreadMgr。</summary>
        public static readonly Guid IID_ITfThreadMgr =
            new Guid("aa80e801-2021-11d2-93e0-0060b067b86e");
        /// <summary>ITfDocumentMgr。</summary>
        public static readonly Guid IID_ITfDocumentMgr =
            new Guid("aa80e7f4-2021-11d2-93e0-0060b067b86e");
        /// <summary>ITfKeystrokeMgr。</summary>
        public static readonly Guid IID_ITfKeystrokeMgr =
            new Guid("aa80e7f0-2021-11d2-93e0-0060b067b86e");
        /// <summary>ITfContext。</summary>
        public static readonly Guid IID_ITfContext =
            new Guid("aa80e7fd-2021-11d2-93e0-0060b067b86e");
        /// <summary>ITfContextView。</summary>
        public static readonly Guid IID_ITfContextView =
            new Guid("2433bf8e-0f9b-435c-ba2c-180611978c30");
        /// <summary>ITfInsertAtSelection。</summary>
        public static readonly Guid IID_ITfInsertAtSelection =
            new Guid("55ce16ba-3014-41c1-9ceb-fade1446ac6c");
        /// <summary>ITfSource。</summary>
        public static readonly Guid IID_ITfSource =
            new Guid("4ea48a35-60ae-446f-8fd6-e6a8d82459f7");
        /// <summary>ITfCompartmentMgr。</summary>
        public static readonly Guid IID_ITfCompartmentMgr =
            new Guid("7dcf57ac-18ad-438b-824d-979bffb74b7c");
        /// <summary>ITfCompartment。</summary>
        public static readonly Guid IID_ITfCompartment =
            new Guid("bb08f7a9-607a-4384-8623-056892b64371");

        // ---- 系统 CLSID ----
        /// <summary>CLSID_TF_ThreadMgr —— 线程管理器，任何 TSF 应用都用 CoCreateInstance 取它。</summary>
        public static readonly Guid CLSID_TF_ThreadMgr =
            new Guid("529a9e6b-6587-4f23-ab9e-9c7d683e3c50");
        /// <summary>CLSID_TF_CategoryMgr。</summary>
        public static readonly Guid CLSID_TF_CategoryMgr =
            new Guid("a4b544a1-438d-4b41-9325-869523e2d6c7");
        /// <summary>CLSID_TF_InputProcessorProfiles。</summary>
        public static readonly Guid CLSID_TF_InputProcessorProfiles =
            new Guid("33c53a50-f456-4884-b049-85fd643ecfed");

        // ---- 语言 / 能力类别（注册脚本里也要用，保持一致） ----
        /// <summary>中文（简体，中国）LANGID。</summary>
        public const ushort LangIdZhCn = 0x0804;

        /// <summary>GUID_TFCAT_TIP_KEYBOARD —— 键盘输入法必需类别。</summary>
        public static readonly Guid GUID_TFCAT_TIP_KEYBOARD =
            new Guid("34745c63-b2f0-4784-8b67-5e12c8701a31");
        /// <summary>GUID_TFCAT_TIPCAP_UIELEMENTENABLED。</summary>
        public static readonly Guid GUID_TFCAT_TIPCAP_UIELEMENTENABLED =
            new Guid("49d2f9ce-1f5e-11d7-a6d3-00065b84435c");
        /// <summary>GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT。</summary>
        public static readonly Guid GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT =
            new Guid("13a016df-560b-46cd-947a-4c3af1e0e35d");
        /// <summary>GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT。</summary>
        public static readonly Guid GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT =
            new Guid("25504fb4-7bab-4bc1-9c69-cf81890f0ef5");
    }

    /// <summary>TSF 编辑会话 / 插入标志位（sdk_msctf.h 原值）。</summary>
    public static class TsfFlags
    {
        /// <summary>TF_ES_SYNC 0x1</summary>
        public const uint TF_ES_SYNC = 0x0001;
        /// <summary>TF_ES_READ 0x2</summary>
        public const uint TF_ES_READ = 0x0002;
        /// <summary>TF_ES_READWRITE 0x6</summary>
        public const uint TF_ES_READWRITE = 0x0006;
        /// <summary>TF_ES_ASYNC 0x8</summary>
        public const uint TF_ES_ASYNC = 0x0008;

        /// <summary>TF_IAS_NOQUERY 0x1</summary>
        public const uint TF_IAS_NOQUERY = 0x0001;
        /// <summary>TF_IAS_QUERYONLY 0x2</summary>
        public const uint TF_IAS_QUERYONLY = 0x0002;
        /// <summary>TF_IAS_NO_DEFAULT_COMPOSITION 0x80000000</summary>
        public const uint TF_IAS_NO_DEFAULT_COMPOSITION = 0x80000000;
    }

    /// <summary>常用 HRESULT。</summary>
    public static class HResult
    {
        public const int S_OK = 0;
        public const int S_FALSE = 1;
        public const int E_NOTIMPL = unchecked((int)0x80004001);
        public const int E_NOINTERFACE = unchecked((int)0x80004002);
        public const int E_POINTER = unchecked((int)0x80004003);
        public const int E_FAIL = unchecked((int)0x80004005);
        public const int E_INVALIDARG = unchecked((int)0x80070057);
    }

    // =====================================================================================
    // 2. 我们实现的接口（TIP 侧）
    // =====================================================================================

    /// <summary>
    /// ITfTextInputProcessor —— 文本服务入口。
    /// Vtbl（相对 IUnknown）：0 Activate / 1 Deactivate。
    /// </summary>
    [ComImport]
    [Guid("aa80e7f7-2021-11d2-93e0-0060b067b86e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfTextInputProcessor
    {
        // 槽位 0
        [PreserveSig]
        int Activate([MarshalAs(UnmanagedType.Interface)] ITfThreadMgr ptim, uint tid);
        // 槽位 1
        [PreserveSig]
        int Deactivate();
    }

    /// <summary>
    /// ITfTextInputProcessorEx —— 比 ITfTextInputProcessor 多一个 ActivateEx。
    /// Vtbl（相对 IUnknown）：0 Activate / 1 Deactivate / 2 ActivateEx。
    /// TSF 会优先 QI 这个接口；不实现它就只能拿到不带 dwFlags 的旧入口。
    ///
    /// ⚠ 这里【故意不写成 `: ITfTextInputProcessor`】——必须扁平声明三个方法。
    /// 原因是 .NET 的 COM 可调用包装（CCW）在「接口继承」形状下生成的 vtable 是坏的，
    /// 已由原生探针（scratch\vtable）逐槽取证确认：
    ///   · 接口继承写法          → Ex 的 vtable 只有 4 槽，槽 3 被绑成 ActivateEx，
    ///                             槽 4/5 是 CLR 内部 pre-code 字节（调了直接 0xC0000005）
    ///   · 扁平写法（本写法）    → 槽 3/4/5 分别正确绑定 Activate/Deactivate/ActivateEx
    /// TSF 正是先 QI 本接口、再调槽 5 的 ActivateEx，所以继承写法会让
    /// 「一选输入法就崩宿主进程」。指纹法实测证据见 scratch\vtable\。
    ///
    /// 另外：本接口扁平化后不再从 ITfTextInputProcessor 继承，所以实现类
    /// （TextService）必须把两个接口都列进基接口列表，这样两个 IID 都能 QI 成功。
    /// </summary>
    [ComImport]
    [Guid("6e4e2102-f9cd-433d-b496-303ce03a6507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfTextInputProcessorEx
    {
        // 槽位 0
        [PreserveSig]
        int Activate([MarshalAs(UnmanagedType.Interface)] ITfThreadMgr ptim, uint tid);
        // 槽位 1
        [PreserveSig]
        int Deactivate();
        // 槽位 2
        [PreserveSig]
        int ActivateEx([MarshalAs(UnmanagedType.Interface)] ITfThreadMgr ptim, uint tid, uint dwFlags);
    }

    /// <summary>
    /// ITfKeyEventSink —— 键盘事件回调。
    /// Vtbl（相对 IUnknown）：
    ///   0 OnSetFocus(BOOL) / 1 OnTestKeyDown / 2 OnTestKeyUp / 3 OnKeyDown / 4 OnKeyUp / 5 OnPreservedKey
    /// OnTestKeyDown 返回 pfEaten=TRUE 表示“我要处理这个键”，宿主不会收到它；
    /// 随后 TSF 会在 OnKeyDown 里再给我们一次。OnTestKeyUp/OnKeyUp 同理。
    /// </summary>
    [ComImport]
    [Guid("aa80e7f5-2021-11d2-93e0-0060b067b86e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfKeyEventSink
    {
        // 槽位 0
        [PreserveSig]
        int OnSetFocus([MarshalAs(UnmanagedType.Bool)] bool fForeground);
        // 槽位 1
        [PreserveSig]
        int OnTestKeyDown([MarshalAs(UnmanagedType.Interface)] ITfContext pic,
                          IntPtr wParam, IntPtr lParam,
                          [MarshalAs(UnmanagedType.Bool)] out bool pfEaten);
        // 槽位 2
        [PreserveSig]
        int OnTestKeyUp([MarshalAs(UnmanagedType.Interface)] ITfContext pic,
                        IntPtr wParam, IntPtr lParam,
                        [MarshalAs(UnmanagedType.Bool)] out bool pfEaten);
        // 槽位 3
        [PreserveSig]
        int OnKeyDown([MarshalAs(UnmanagedType.Interface)] ITfContext pic,
                      IntPtr wParam, IntPtr lParam,
                      [MarshalAs(UnmanagedType.Bool)] out bool pfEaten);
        // 槽位 4
        [PreserveSig]
        int OnKeyUp([MarshalAs(UnmanagedType.Interface)] ITfContext pic,
                    IntPtr wParam, IntPtr lParam,
                    [MarshalAs(UnmanagedType.Bool)] out bool pfEaten);
        // 槽位 5
        [PreserveSig]
        int OnPreservedKey([MarshalAs(UnmanagedType.Interface)] ITfContext pic,
                           ref Guid rguid,
                           [MarshalAs(UnmanagedType.Bool)] out bool pfEaten);
    }

    /// <summary>
    /// ITfThreadMgrEventSink —— 焦点/文档管理器变更回调。
    /// Vtbl（相对 IUnknown）：
    ///   0 OnInitDocumentMgr / 1 OnUninitDocumentMgr / 2 OnSetFocus / 3 OnPushContext / 4 OnPopContext
    /// </summary>
    [ComImport]
    [Guid("aa80e80e-2021-11d2-93e0-0060b067b86e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfThreadMgrEventSink
    {
        // 槽位 0
        [PreserveSig]
        int OnInitDocumentMgr([MarshalAs(UnmanagedType.Interface)] ITfDocumentMgr pdim);
        // 槽位 1
        [PreserveSig]
        int OnUninitDocumentMgr([MarshalAs(UnmanagedType.Interface)] ITfDocumentMgr pdim);
        // 槽位 2
        [PreserveSig]
        int OnSetFocus([MarshalAs(UnmanagedType.Interface)] ITfDocumentMgr pdimFocus,
                       [MarshalAs(UnmanagedType.Interface)] ITfDocumentMgr pdimPrevFocus);
        // 槽位 3
        [PreserveSig]
        int OnPushContext([MarshalAs(UnmanagedType.Interface)] ITfContext pic);
        // 槽位 4
        [PreserveSig]
        int OnPopContext([MarshalAs(UnmanagedType.Interface)] ITfContext pic);
    }

    /// <summary>
    /// ITfEditSession —— 编辑会话回调。
    /// Vtbl（相对 IUnknown）：0 DoEditSession(TfEditCookie ec)。
    /// ITfContext::RequestEditSession 会在文档未被别人占用时回调这个方法，
    /// 此时 ec 才有效，才可以对 ITfRange / ITfInsertAtSelection 做写操作。
    /// </summary>
    [ComImport]
    [Guid("aa80e803-2021-11d2-93e0-0060b067b86e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfEditSession
    {
        // 槽位 0
        [PreserveSig]
        int DoEditSession(uint ec);
    }

    // =====================================================================================
    // 3. 我们调用的接口（应用侧）
    // =====================================================================================

    /// <summary>
    /// ITfThreadMgr。
    /// Vtbl（相对 IUnknown）：0 Activate / 1 Deactivate / 2 CreateDocumentMgr / 3 EnumDocumentMgrs /
    ///   4 GetFocus / 5 SetFocus / 6 AssociateFocus / 7 IsThreadFocus / 8 GetFunctionProvider /
    ///   9 EnumFunctionProviders / 10 GetGlobalCompartment
    /// </summary>
    [ComImport]
    [Guid("aa80e801-2021-11d2-93e0-0060b067b86e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfThreadMgr
    {
        // 槽位 0
        [PreserveSig]
        int Activate(out uint ptid);
        // 槽位 1
        [PreserveSig]
        int Deactivate();
        // 槽位 2
        [PreserveSig]
        int CreateDocumentMgr([MarshalAs(UnmanagedType.Interface)] out ITfDocumentMgr ppdim);
        // 槽位 3
        [PreserveSig]
        int EnumDocumentMgrs(out IntPtr ppEnum);
        // 槽位 4
        [PreserveSig]
        int GetFocus([MarshalAs(UnmanagedType.Interface)] out ITfDocumentMgr ppdimFocus);
        // 槽位 5
        [PreserveSig]
        int SetFocus([MarshalAs(UnmanagedType.Interface)] ITfDocumentMgr pdimFocus);
        // 槽位 6
        [PreserveSig]
        int AssociateFocus(IntPtr hwnd,
                           [MarshalAs(UnmanagedType.Interface)] ITfDocumentMgr pdimNew,
                           [MarshalAs(UnmanagedType.Interface)] out ITfDocumentMgr ppdimPrev);
        // 槽位 7
        [PreserveSig]
        int IsThreadFocus([MarshalAs(UnmanagedType.Bool)] out bool pfThreadFocus);
        // 槽位 8
        [PreserveSig]
        int GetFunctionProvider(ref Guid clsid, out IntPtr ppFuncProv);
        // 槽位 9
        [PreserveSig]
        int EnumFunctionProviders(out IntPtr ppEnum);
        // 槽位 10
        [PreserveSig]
        int GetGlobalCompartment([MarshalAs(UnmanagedType.Interface)] out ITfCompartmentMgr ppCompMgr);
    }

    /// <summary>
    /// ITfDocumentMgr。
    /// Vtbl（相对 IUnknown）：0 CreateContext / 1 Push / 2 Pop / 3 GetTop / 4 GetBase / 5 EnumContexts
    /// </summary>
    [ComImport]
    [Guid("aa80e7f4-2021-11d2-93e0-0060b067b86e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfDocumentMgr
    {
        // 槽位 0
        [PreserveSig]
        int CreateContext(uint tidOwner, uint dwFlags,
                          [MarshalAs(UnmanagedType.IUnknown)] object punk,
                          [MarshalAs(UnmanagedType.Interface)] out ITfContext ppic,
                          out uint pecTextStore);
        // 槽位 1
        [PreserveSig]
        int Push([MarshalAs(UnmanagedType.Interface)] ITfContext pic);
        // 槽位 2
        [PreserveSig]
        int Pop(uint dwFlags);
        // 槽位 3
        [PreserveSig]
        int GetTop([MarshalAs(UnmanagedType.Interface)] out ITfContext ppic);
        // 槽位 4
        [PreserveSig]
        int GetBase([MarshalAs(UnmanagedType.Interface)] out ITfContext ppic);
        // 槽位 5
        [PreserveSig]
        int EnumContexts(out IntPtr ppEnum);
    }

    /// <summary>
    /// ITfKeystrokeMgr。
    /// Vtbl（相对 IUnknown）：
    ///   0 AdviseKeyEventSink / 1 UnadviseKeyEventSink / 2 GetForeground / 3 TestKeyDown / 4 TestKeyUp /
    ///   5 KeyDown / 6 KeyUp / 7 GetPreservedKey / 8 IsPreservedKey / 9 PreserveKey / 10 UnpreserveKey /
    ///   11 SetPreservedKeyDescription / 12 GetPreservedKeyDescription / 13 SimulatePreservedKey
    /// </summary>
    [ComImport]
    [Guid("aa80e7f0-2021-11d2-93e0-0060b067b86e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfKeystrokeMgr
    {
        // 槽位 0
        [PreserveSig]
        int AdviseKeyEventSink(uint tid,
                               [MarshalAs(UnmanagedType.Interface)] ITfKeyEventSink pSink,
                               [MarshalAs(UnmanagedType.Bool)] bool fForeground);
        // 槽位 1
        [PreserveSig]
        int UnadviseKeyEventSink(uint tid);
        // 槽位 2
        [PreserveSig]
        int GetForeground(out Guid pclsid);
        // 槽位 3
        [PreserveSig]
        int TestKeyDown(IntPtr wParam, IntPtr lParam, [MarshalAs(UnmanagedType.Bool)] out bool pfEaten);
        // 槽位 4
        [PreserveSig]
        int TestKeyUp(IntPtr wParam, IntPtr lParam, [MarshalAs(UnmanagedType.Bool)] out bool pfEaten);
        // 槽位 5
        [PreserveSig]
        int KeyDown(IntPtr wParam, IntPtr lParam, [MarshalAs(UnmanagedType.Bool)] out bool pfEaten);
        // 槽位 6
        [PreserveSig]
        int KeyUp(IntPtr wParam, IntPtr lParam, [MarshalAs(UnmanagedType.Bool)] out bool pfEaten);
        // 槽位 7
        [PreserveSig]
        int GetPreservedKey([MarshalAs(UnmanagedType.Interface)] ITfContext pic,
                            IntPtr pprekey, out Guid pguid);
        // 槽位 8
        [PreserveSig]
        int IsPreservedKey(ref Guid rguid, IntPtr pprekey,
                           [MarshalAs(UnmanagedType.Bool)] out bool pfRegistered);
        // 槽位 9
        [PreserveSig]
        int PreserveKey(uint tid, ref Guid rguid, IntPtr prekey,
                        [MarshalAs(UnmanagedType.LPWStr)] string pchDesc, uint cchDesc);
        // 槽位 10
        [PreserveSig]
        int UnpreserveKey(ref Guid rguid, IntPtr pprekey);
        // 槽位 11
        [PreserveSig]
        int SetPreservedKeyDescription(ref Guid rguid,
                                       [MarshalAs(UnmanagedType.LPWStr)] string pchDesc, uint cchDesc);
        // 槽位 12
        [PreserveSig]
        int GetPreservedKeyDescription(ref Guid rguid,
                                       [MarshalAs(UnmanagedType.BStr)] out string pbstrDesc);
        // 槽位 13
        [PreserveSig]
        int SimulatePreservedKey([MarshalAs(UnmanagedType.Interface)] ITfContext pic,
                                 ref Guid rguid,
                                 [MarshalAs(UnmanagedType.Bool)] out bool pfEaten);
    }

    /// <summary>
    /// ITfContext。
    /// Vtbl（相对 IUnknown）：0 RequestEditSession / 1 InWriteSession / 2 GetSelection / 3 SetSelection /
    ///   4 GetStart / 5 GetEnd / 6 GetActiveView / 7 EnumViews / 8 GetStatus / 9 GetProperty /
    ///   10 GetAppProperty / 11 TrackProperties / 12 EnumProperties / 13 GetDocumentMgr / 14 CreateRangeBackup
    /// </summary>
    [ComImport]
    [Guid("aa80e7fd-2021-11d2-93e0-0060b067b86e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfContext
    {
        // 槽位 0
        [PreserveSig]
        int RequestEditSession(uint tid,
                               [MarshalAs(UnmanagedType.Interface)] ITfEditSession pes,
                               uint dwFlags,
                               out int phrSession);
        // 槽位 1
        [PreserveSig]
        int InWriteSession(uint tid, [MarshalAs(UnmanagedType.Bool)] out bool pfWriteSession);
        // 槽位 2
        [PreserveSig]
        int GetSelection(uint ec, uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched);
        // 槽位 3
        [PreserveSig]
        int SetSelection(uint ec, uint ulCount, IntPtr pSelection);
        // 槽位 4
        [PreserveSig]
        int GetStart(uint ec, [MarshalAs(UnmanagedType.Interface)] out ITfRange ppStart);
        // 槽位 5
        [PreserveSig]
        int GetEnd(uint ec, [MarshalAs(UnmanagedType.Interface)] out ITfRange ppEnd);
        // 槽位 6
        [PreserveSig]
        int GetActiveView([MarshalAs(UnmanagedType.Interface)] out ITfContextView ppView);
        // 槽位 7
        [PreserveSig]
        int EnumViews(out IntPtr ppEnum);
        // 槽位 8
        [PreserveSig]
        int GetStatus(IntPtr pdcs);
        // 槽位 9
        [PreserveSig]
        int GetProperty(ref Guid guidProp, out IntPtr ppProp);
        // 槽位 10
        [PreserveSig]
        int GetAppProperty(ref Guid guidProp, out IntPtr ppProp);
        // 槽位 11
        [PreserveSig]
        int TrackProperties(IntPtr prgProp, uint cProp, IntPtr prgAppProp, uint cAppProp, out IntPtr ppProperty);
        // 槽位 12
        [PreserveSig]
        int EnumProperties(out IntPtr ppEnum);
        // 槽位 13
        [PreserveSig]
        int GetDocumentMgr([MarshalAs(UnmanagedType.Interface)] out ITfDocumentMgr ppDm);
        // 槽位 14
        [PreserveSig]
        int CreateRangeBackup(uint ec,
                              [MarshalAs(UnmanagedType.Interface)] ITfRange pRange,
                              out IntPtr ppBackup);
    }

    /// <summary>
    /// ITfContextView。
    /// Vtbl（相对 IUnknown）：0 GetRangeFromPoint / 1 GetTextExt / 2 GetScreenExt / 3 GetWnd
    /// </summary>
    [ComImport]
    [Guid("2433bf8e-0f9b-435c-ba2c-180611978c30")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfContextView
    {
        // 槽位 0
        [PreserveSig]
        int GetRangeFromPoint(uint ec, ref POINT ppt, uint dwFlags,
                              [MarshalAs(UnmanagedType.Interface)] out ITfRange ppRange);
        // 槽位 1
        [PreserveSig]
        int GetTextExt(uint ec, [MarshalAs(UnmanagedType.Interface)] ITfRange pRange,
                       out RECT prc, [MarshalAs(UnmanagedType.Bool)] out bool pfClipped);
        // 槽位 2
        [PreserveSig]
        int GetScreenExt(out RECT prc);
        // 槽位 3
        [PreserveSig]
        int GetWnd(out IntPtr phwnd);
    }

    /// <summary>
    /// ITfRange。只需 GetText/SetText 就够，但槽位必须写全。
    /// Vtbl（相对 IUnknown）：0 GetText / 1 SetText / 2 GetFormattedText / 3 GetEmbedded / 4 InsertEmbedded /
    ///   5 ShiftStart / 6 ShiftEnd / 7 ShiftStartToRange / 8 ShiftEndToRange / 9 ShiftStartRegion /
    ///   10 ShiftEndRegion / 11 IsEmpty / 12 Collapse / 13 IsEqualStart / 14 IsEqualEnd / 15 CompareStart /
    ///   16 CompareEnd / 17 AdjustForInsert / 18 GetGravity / 19 SetGravity / 20 Clone / 21 GetContext
    /// </summary>
    [ComImport]
    [Guid("aa80e7ff-2021-11d2-93e0-0060b067b86e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfRange
    {
        // 槽位 0
        [PreserveSig]
        int GetText(uint ec, uint dwFlags, IntPtr pchText, uint cchMax, out uint pcch);
        // 槽位 1
        [PreserveSig]
        int SetText(uint ec, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string pchText, int cch);
        // 槽位 2
        [PreserveSig]
        int GetFormattedText(uint ec, out IntPtr ppDataObject);
        // 槽位 3
        [PreserveSig]
        int GetEmbedded(uint ec, ref Guid rguidService, ref Guid riid, out IntPtr ppunk);
        // 槽位 4
        [PreserveSig]
        int InsertEmbedded(uint ec, uint dwFlags, IntPtr pDataObject);
        // 槽位 5
        [PreserveSig]
        int ShiftStart(uint ec, int cchReq, out int pcch, IntPtr pHalt);
        // 槽位 6
        [PreserveSig]
        int ShiftEnd(uint ec, int cchReq, out int pcch, IntPtr pHalt);
        // 槽位 7
        [PreserveSig]
        int ShiftStartToRange(uint ec, [MarshalAs(UnmanagedType.Interface)] ITfRange pRange, int aPos);
        // 槽位 8
        [PreserveSig]
        int ShiftEndToRange(uint ec, [MarshalAs(UnmanagedType.Interface)] ITfRange pRange, int aPos);
        // 槽位 9
        [PreserveSig]
        int ShiftStartRegion(uint ec, int dir, [MarshalAs(UnmanagedType.Bool)] out bool pfNoRegion);
        // 槽位 10
        [PreserveSig]
        int ShiftEndRegion(uint ec, int dir, [MarshalAs(UnmanagedType.Bool)] out bool pfNoRegion);
        // 槽位 11
        [PreserveSig]
        int IsEmpty(uint ec, [MarshalAs(UnmanagedType.Bool)] out bool pfEmpty);
        // 槽位 12
        [PreserveSig]
        int Collapse(uint ec, int aPos);
        // 槽位 13
        [PreserveSig]
        int IsEqualStart(uint ec, [MarshalAs(UnmanagedType.Interface)] ITfRange pWith, int aPos,
                         [MarshalAs(UnmanagedType.Bool)] out bool pfEqual);
        // 槽位 14
        [PreserveSig]
        int IsEqualEnd(uint ec, [MarshalAs(UnmanagedType.Interface)] ITfRange pWith, int aPos,
                       [MarshalAs(UnmanagedType.Bool)] out bool pfEqual);
        // 槽位 15
        [PreserveSig]
        int CompareStart(uint ec, [MarshalAs(UnmanagedType.Interface)] ITfRange pWith, int aPos, out int plResult);
        // 槽位 16
        [PreserveSig]
        int CompareEnd(uint ec, [MarshalAs(UnmanagedType.Interface)] ITfRange pWith, int aPos, out int plResult);
        // 槽位 17
        [PreserveSig]
        int AdjustForInsert(uint ec, uint cchInsert, [MarshalAs(UnmanagedType.Bool)] out bool pfInsertOk);
        // 槽位 18
        [PreserveSig]
        int GetGravity(out int pgStart, out int pgEnd);
        // 槽位 19
        [PreserveSig]
        int SetGravity(uint ec, int gStart, int gEnd);
        // 槽位 20
        [PreserveSig]
        int Clone([MarshalAs(UnmanagedType.Interface)] out ITfRange ppClone);
        // 槽位 21
        [PreserveSig]
        int GetContext([MarshalAs(UnmanagedType.Interface)] out ITfContext ppContext);
    }

    /// <summary>
    /// ITfInsertAtSelection —— 在当前选区插入文本。
    /// Vtbl（相对 IUnknown）：0 InsertTextAtSelection / 1 InsertEmbeddedAtSelection
    /// 这是本工程提交文本的主路径：不需要 TF_SELECTION 那套结构体封送，风险最低。
    /// </summary>
    [ComImport]
    [Guid("55ce16ba-3014-41c1-9ceb-fade1446ac6c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfInsertAtSelection
    {
        // 槽位 0
        [PreserveSig]
        int InsertTextAtSelection(uint ec, uint dwFlags,
                                  [MarshalAs(UnmanagedType.LPWStr)] string pchText, int cch,
                                  [MarshalAs(UnmanagedType.Interface)] out ITfRange ppRange);
        // 槽位 1
        [PreserveSig]
        int InsertEmbeddedAtSelection(uint ec, uint dwFlags, IntPtr pDataObject,
                                      [MarshalAs(UnmanagedType.Interface)] out ITfRange ppRange);
    }

    /// <summary>
    /// ITfSource。Vtbl（相对 IUnknown）：0 AdviseSink / 1 UnadviseSink
    /// </summary>
    [ComImport]
    [Guid("4ea48a35-60ae-446f-8fd6-e6a8d82459f7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfSource
    {
        // 槽位 0
        [PreserveSig]
        int AdviseSink(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] object punk, out uint pdwCookie);
        // 槽位 1
        [PreserveSig]
        int UnadviseSink(uint dwCookie);
    }

    /// <summary>
    /// ITfCompartmentMgr。Vtbl（相对 IUnknown）：0 GetCompartment / 1 ClearCompartment / 2 EnumCompartments
    /// </summary>
    [ComImport]
    [Guid("7dcf57ac-18ad-438b-824d-979bffb74b7c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfCompartmentMgr
    {
        // 槽位 0
        [PreserveSig]
        int GetCompartment(ref Guid rguid, [MarshalAs(UnmanagedType.Interface)] out ITfCompartment ppcomp);
        // 槽位 1
        [PreserveSig]
        int ClearCompartment(uint tid, ref Guid rguid);
        // 槽位 2
        [PreserveSig]
        int EnumCompartments(out IntPtr ppEnum);
    }

    /// <summary>
    /// ITfCompartment。Vtbl（相对 IUnknown）：0 SetValue / 1 GetValue
    /// VARIANT 用 IntPtr 透传（本工程只读/写原子的状态腔，不做深度封送）。
    /// </summary>
    [ComImport]
    [Guid("bb08f7a9-607a-4384-8623-056892b64371")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfCompartment
    {
        // 槽位 0
        [PreserveSig]
        int SetValue(uint tid, IntPtr pvarValue);
        // 槽位 1
        [PreserveSig]
        int GetValue(IntPtr pvarValue);
    }

    // =====================================================================================
    // 4. 结构体（布局严格对齐 sdk_msctf.idl / windef.h）
    // =====================================================================================

    /// <summary>Win32 POINT。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
        public POINT(int x, int y) { this.x = x; this.y = y; }
    }

    /// <summary>Win32 RECT。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    /// <summary>Win32 GUITHREADINFO —— 用来取真实插入符位置（比 TF_SELECTION 封送安全得多）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    // =====================================================================================
    // 5. Win32 / TSF 原生导入
    // =====================================================================================

    internal static class TsfNative
    {
        /// <summary>取当前线程（也就是宿主 UI 线程）的 GUI 状态，含插入符矩形。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetFocus();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetForegroundWindow();

        /// <summary>
        /// 取当前线程的线程管理器。等价于 CoCreateInstance(CLSID_TF_ThreadMgr)。
        /// 用于自检工具（不需要注册就能驱动真实 msctf.dll）。
        /// </summary>
        [DllImport("msctf.dll", PreserveSig = false)]
        internal static extern void TF_GetThreadMgr([MarshalAs(UnmanagedType.Interface)] out ITfThreadMgr ppdim);

        /// <summary>
        /// TF_CreateInputProcessorProfiles —— 不需要注册即可拿到的注册对象。
        /// 注意：用它能成功注册到 HKLM 仍然需要管理员权限，我们只把它用于自检。
        /// </summary>
        [DllImport("msctf.dll", PreserveSig = false)]
        internal static extern void TF_CreateInputProcessorProfiles(
            [MarshalAs(UnmanagedType.Interface)] out object ppa);

        /// <summary>调出系统"文本服务和输入语言"对话框。</summary>
        [DllImport("input.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InstallLayoutOrTip(string psz, uint dwFlags);
    }
}
