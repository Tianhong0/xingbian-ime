// 形变输入法 —— TSF 文本服务本体
//
// 注册表里的 ClassFullName 是 "Xingbian.Tsf.TextService"（见 XingbianTsfIds.ClassFullName），
// 所以本类的命名空间 + 类名不能改。
//
// ============================ 为什么 v1 不做 TSF composition ============================
// 标准输入法会在文档里建立一个 TSF composition（带下划线的未决文本），再通过
// ITfComposition / ITfContextComposition / ITfDisplayAttributeProvider 把拼音串画在文档里。
// 这条链路要求：RequestEditSession 成功 → StartComposition → 每敲一键都要 ShiftStart/ShiftEnd
// 调整 composition range → 还要注册 display attribute 才能让文字带下划线 → 取消/异常路径下
// 必须保证 composition 一定被 EndComposition，否则整个文档被锁死、宿主程序卡住。
// 任何一步在真实宿主（记事本 / 浏览器 / Office，各自 TSF 实现细节不同）里出问题，
// 表现都是「宿主卡死」，排查成本极高。
//
// 本实现改为：**自有缓冲区 + 无焦点候选浮窗（CandidatePanel）显示拼音与候选**，
// 文档里一个字符都不写入，直到用户按下空格/回车/数字键选定了候选，
// 才在**一次**编辑会话里把最终文本整段插入。这样：
//   · 编辑会话只出现在提交那一瞬间，没有「跨多次按键持续持有的 composition」
//   · 任何时刻崩溃都只影响我们自己的浮窗，不会把宿主文档留在锁定状态
//   · 仍然是一个注册进 Win+空格 输入法列表、由 msctf.dll 加载的**真 TSF 文本服务**
// ======================================================================================
//
// 零容错约束：本类的每个方法都会被原生 msctf.dll 在宿主进程内直接调用。
// 任何一个逃逸到 COM 边界的托管异常都会直接杀掉宿主进程（记事本、浏览器…），
// 所以每个入口都整体 try/catch，并且一律返回 S_OK。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Xingbian.Core;
using Xingbian.Tsf.Interop;

namespace Xingbian.Tsf
{
    /// <summary>
    /// 形变输入法文本服务。把输入的拼音转成「相近形体拆分」的候选，
    /// 例如 nima → 你妈 → 亻尔 女马。
    /// </summary>
    /// <remarks>
    /// 同时实现四个接口：
    ///   ITfTextInputProcessorEx —— TSF 加载/卸载我们的入口
    ///   ITfKeyEventSink         —— 键盘事件（拼音输入在这里发生）
    ///   ITfThreadMgrEventSink   —— 焦点变化（失焦时清理状态、收起浮窗）
    ///   ITfEditSession          —— 提交文本用的编辑会话
    ///
    /// 后三个接口都是 [local] 接口（没有 proxy/stub，不能跨进程封送）。
    /// 可行性的前提是 TIP 由宿主进程**进程内**加载（InprocServer32 → mscoree.dll），
    /// 此时 CLR 的 CCW 直接按 vtable 槽位调用托管方法，不需要任何封送。
    /// 这一点已用 scratch\ccwtest 实测验证：对托管对象取 ITfKeyEventSink 的 CCW 指针，
    /// QueryInterface 返回 hr=0 且 IID 确为 aa80e7f5-2021-11d2-93e0-0060b067b86e。
    /// </remarks>
    [ComVisible(true)]
    [Guid(XingbianTsfIds.Clsid)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class TextService :
        ITfTextInputProcessor,ITfTextInputProcessorEx,
        ITfKeyEventSink,
        ITfThreadMgrEventSink,
        ITfEditSession
    {
        // =================================================================================
        // 常量
        // =================================================================================

        /// <summary>一次最多展示的候选数（与数字键 1..9 一一对应）。</summary>
        private const int MaxCandidates = 9;

        /// <summary>翻页时每次跳过的候选数。</summary>
        private const int PageStep = 5;

        // 虚拟键码
        private const int VK_BACK = 0x08;
        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_PRIOR = 0x21;   // PageUp
        private const int VK_NEXT = 0x22;    // PageDown
        private const int VK_END = 0x23;
        private const int VK_HOME = 0x24;
        private const int VK_LEFT = 0x25;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;
        private const int VK_SHIFT = 0x10;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;    // Alt

        // =================================================================================
        // 共享引擎（进程级缓存）
        // =================================================================================

        // 词典 ~53MB、加载 ~240ms。TSF 会在焦点变化时反复 Activate/Deactivate 文本服务，
        // 所以绝不能每次 Activate 都重新加载 —— 必须在进程内缓存。
        // DictFile 加载后是只读的且支持并发读，多实例共享安全。
        private static readonly object s_engineLock = new object();
        private static XingbianEngine s_engine;
        private static bool s_engineAttempted;
        private static string s_engineError;

        /// <summary>词典加载失败的原因（未尝试或成功时为 null）。用于自检与排障。</summary>
        public static string EngineError { get { return s_engineError; } }

        /// <summary>
        /// 预先加载词典。自检工具用它把加载失败与按键路径分开观察。
        /// </summary>
        public static bool Warmup(out string error)
        {
            XingbianEngine engine = EnsureEngine();
            error = s_engineError;
            return engine != null;
        }

        /// <summary>
        /// 取共享引擎；只在首次调用时真正加载，失败后不再重试（避免每次按键都卡 240ms）。
        /// </summary>
        private static XingbianEngine EnsureEngine()
        {
            if (s_engine != null) { return s_engine; }
            if (s_engineAttempted) { return null; }

            lock (s_engineLock)
            {
                if (s_engine != null) { return s_engine; }
                if (s_engineAttempted) { return null; }
                s_engineAttempted = true;
                try
                {
                    s_engine = XingbianEngine.CreateDefault();
                    s_engineError = null;
                }
                catch (Exception ex)
                {
                    s_engine = null;
                    s_engineError = ex.GetType().Name + ": " + ex.Message;
                }
                return s_engine;
            }
        }

        // =================================================================================
        // 实例状态（全部只在宿主 UI 线程上访问，无需加锁）
        // =================================================================================

        private ITfThreadMgr _threadMgr;
        private ITfKeystrokeMgr _keystrokeMgr;
        private ITfSource _threadMgrSource;
        private uint _tid;
        private uint _threadMgrSinkCookie = uint.MaxValue;
        private bool _keySinkAdvised;
        private bool _active;               // ActivateEx 成功 ~ Deactivate
        private bool _chineseMode = true;   // 单击 Shift 切换中/英

        private readonly StringBuilder _buffer = new StringBuilder(32);   // 已输入的拼音字母
        private IList<Candidate> _candidates;                             // 当前候选
        private int _selectedIndex;                                       // 默认 0 = 首候选

        // 英文模式下单击 Shift 切回中文；Shift 与其它键组合时不当成切换
        private bool _shiftPending;
        private bool _shiftChord;

        private CandidatePanel _panel;
        private bool _panelClickHooked;

        // 宿主 UI 线程 id（ActivateEx 时记录）。候选窗的窗口就建在这个线程上，
        // 所以它的鼠标消息回调也必然回到这个线程；这里记下来是为了在
        // ItemClicked 里做一次防御性断言，绝不从别的线程碰实例状态。
        private uint _uiThreadId;

        // 提交文本用的暂存（ITfEditSession::DoEditSession 与调用方在同一线程、同步执行）
        private string _pendingInsert;
        private int _pendingInsertHr;

        // =================================================================================
        // ITfTextInputProcessorEx
        // =================================================================================

        /// <summary>
        /// TSF 激活文本服务。ptim 是宿主线程的 ITfThreadMgr，tid 是本服务的客户端 ID。
        /// </summary>
        public int ActivateEx(ITfThreadMgr ptim, uint tid, uint dwFlags)
        {
            try
            {
                if (ptim == null) { return HResult.E_INVALIDARG; }

                _threadMgr = ptim;
                _tid = tid;

                // 记下宿主 UI 线程：候选窗建在这个线程上，它的鼠标回调也回到这里，
                // ItemClicked 里据此确保不在别的线程上碰实例状态。
                _uiThreadId = (uint)Thread.CurrentThread.ManagedThreadId;

                // 焦点变化通知：失焦时清理缓冲、收起浮窗。
                // AdviseSink 要求传 IUnknown，TSF 会自己 QI 到 riid；
                // 我们（TextService）实现了 ITfThreadMgrEventSink，所以 this 就能被 QI 到。
                _threadMgrSource = ptim as ITfSource;
                if (_threadMgrSource != null)
                {
                    Guid riid = TsfIids.IID_ITfThreadMgrEventSink;
                    uint cookie;
                    int hr = _threadMgrSource.AdviseSink(ref riid, this, out cookie);
                    if (hr == HResult.S_OK) { _threadMgrSinkCookie = cookie; }
                }

                // 键盘事件：ITfKeystrokeMgr 由线程管理器对象实现，QI 即可。
                _keystrokeMgr = ptim as ITfKeystrokeMgr;
                if (_keystrokeMgr != null)
                {
                    int hr = _keystrokeMgr.AdviseKeyEventSink(tid, this, true);
                    if (hr == HResult.S_OK) { _keySinkAdvised = true; }
                }

                _active = true;
                ResetInput();
                return HResult.S_OK;
            }
            catch (Exception)
            {
                // 绝不让异常穿过 COM 边界 —— 那会杀掉宿主进程（记事本 / 浏览器）。
                return HResult.S_OK;
            }
        }

        /// <summary>ITfTextInputProcessor::Activate —— 旧入口，等价于 ActivateEx(ptim, tid, 0)。</summary>
        public int Activate(ITfThreadMgr ptim, uint tid)
        {
            return ActivateEx(ptim, tid, 0);
        }

        /// <summary>TSF 卸载文本服务。必须把所有回调注销干净，否则宿主进程会留住我们的对象。</summary>
        public int Deactivate()
        {
            try
            {
                ResetInput();
                DestroyPanel();

                if (_keySinkAdvised && _keystrokeMgr != null)
                {
                    try { _keystrokeMgr.UnadviseKeyEventSink(_tid); }
                    catch (Exception) { }
                    _keySinkAdvised = false;
                }

                if (_threadMgrSinkCookie != uint.MaxValue && _threadMgrSource != null)
                {
                    try { _threadMgrSource.UnadviseSink(_threadMgrSinkCookie); }
                    catch (Exception) { }
                    _threadMgrSinkCookie = uint.MaxValue;
                }

                _threadMgrSource = null;
                _keystrokeMgr = null;
                _threadMgr = null;
                _active = false;
                return HResult.S_OK;
            }
            catch (Exception)
            {
                return HResult.S_OK;
            }
        }

        // =================================================================================
        // ITfKeyEventSink
        // =================================================================================

        /// <summary>线程/前台焦点变化。失去前台焦点时必须把浮窗收起来。</summary>
        public int OnSetFocus(bool fForeground)
        {
            try
            {
                if (!fForeground) { ResetInput(); HidePanel(); }
            }
            catch (Exception) { }
            return HResult.S_OK;
        }

        /// <summary>
        /// 询问「这个键你要不要吃掉」。返回 true 时宿主拿不到该键，
        /// 随后 TSF 会再调一次 OnKeyDown 让我们真正处理。
        /// 判定必须与 OnKeyDown 完全一致，否则会出现「说要吃但没处理」或反之。
        /// </summary>
        public int OnTestKeyDown(ITfContext pic, IntPtr wParam, IntPtr lParam, out bool pfEaten)
        {
            pfEaten = false;
            try
            {
                pfEaten = ShouldEat(GetVirtualKey(wParam));
            }
            catch (Exception) { }
            return HResult.S_OK;
        }

        /// <summary>键按下：真正的状态机在这里。</summary>
        public int OnKeyDown(ITfContext pic, IntPtr wParam, IntPtr lParam, out bool pfEaten)
        {
            pfEaten = false;
            try
            {
                int vk = GetVirtualKey(wParam);
                if (!ShouldEat(vk)) { return HResult.S_OK; }

                // 我们吃掉这个键，宿主不会收到（也就不会产生 WM_CHAR）。
                pfEaten = true;

                if (IsLetterKey(vk))
                {
                    AppendLetter(LetterFromKey(vk), pic);
                    return HResult.S_OK;
                }

                // Shift 只是被记录，不在这里处理；与字母组合时标记为「组合键」。
                _shiftChord = true;

                switch (vk)
                {
                    case VK_BACK:
                        Backspace(pic);
                        break;

                    case VK_SPACE:
                    case VK_RETURN:
                        CommitSelected(pic);
                        break;

                    case VK_ESCAPE:
                        ResetInput();
                        HidePanel();
                        break;

                    case VK_UP:
                        MoveSelection(-1, pic);
                        break;

                    case VK_DOWN:
                        MoveSelection(1, pic);
                        break;

                    case VK_PRIOR:
                        MoveSelection(-PageStep, pic);
                        break;

                    case VK_NEXT:
                        MoveSelection(PageStep, pic);
                        break;

                    case VK_HOME:
                        if (_candidates != null && _candidates.Count > 0)
                        {
                            _selectedIndex = 0;
                            UpdatePanel(pic);
                        }
                        break;

                    case VK_END:
                        if (_candidates != null && _candidates.Count > 0)
                        {
                            _selectedIndex = _candidates.Count - 1;
                            UpdatePanel(pic);
                        }
                        break;

                    default:
                        // 数字键 1..9 直接选中对应候选并提交
                        if (vk >= 0x31 && vk <= 0x39)
                        {
                            SelectByDigit(vk - 0x30, pic);
                        }
                        break;
                }
                return HResult.S_OK;
            }
            catch (Exception)
            {
                // 出错时至少把缓冲清干净，别让用户卡在一个再也提交不了的半截状态。
                try { ResetInput(); HidePanel(); } catch (Exception) { }
                return HResult.S_OK;
            }
        }

        /// <summary>键抬起。除了单击 Shift 切换中英文，其余一律放行。</summary>
        public int OnKeyUp(ITfContext pic, IntPtr wParam, IntPtr lParam, out bool pfEaten)
        {
            pfEaten = false;
            try
            {
                int vk = GetVirtualKey(wParam);
                if (vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT)
                {
                    // 单独按一下 Shift（中间没按别的键）→ 切换中/英文。
                    // 注意不吞这个键：让宿主正常收到 Shift 的 up。
                    if (_shiftPending && !_shiftChord)
                    {
                        ResetInput();
                        HidePanel();
                        _chineseMode = !_chineseMode;
                    }
                    _shiftPending = false;
                    _shiftChord = false;
                }
            }
            catch (Exception) { }
            return HResult.S_OK;
        }

        /// <summary>键抬起的预检。我们不吞任何键抬起事件。</summary>
        public int OnTestKeyUp(ITfContext pic, IntPtr wParam, IntPtr lParam, out bool pfEaten)
        {
            pfEaten = false;
            try
            {
                int vk = GetVirtualKey(wParam);
                if (vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT)
                {
                    // 记录「Shift 被单独按下」，OnKeyUp 里据此判断是否切换。
                    if (!_shiftChord) { _shiftPending = true; }
                }
                else if (ShouldEat(vk))
                {
                    _shiftChord = true;
                }
            }
            catch (Exception) { }
            return HResult.S_OK;
        }

        /// <summary>保留键回调。本实现不注册任何保留键。</summary>
        public int OnPreservedKey(ITfContext pic, ref Guid rguid, out bool pfEaten)
        {
            pfEaten = false;
            return HResult.S_OK;
        }

        // =================================================================================
        // ITfThreadMgrEventSink
        // =================================================================================

        public int OnInitDocumentMgr(ITfDocumentMgr pdim) { return HResult.S_OK; }

        public int OnUninitDocumentMgr(ITfDocumentMgr pdim) { return HResult.S_OK; }

        /// <summary>文档焦点变化；pdimFocus 为 null 表示整个线程失去了焦点。</summary>
        public int OnSetFocus(ITfDocumentMgr pdimFocus, ITfDocumentMgr pdimPrevFocus)
        {
            try
            {
                if (pdimFocus == null) { ResetInput(); HidePanel(); }
            }
            catch (Exception) { }
            return HResult.S_OK;
        }

        public int OnPushContext(ITfContext pic) { return HResult.S_OK; }

        public int OnPopContext(ITfContext pic) { return HResult.S_OK; }

        // =================================================================================
        // ITfEditSession —— 唯一真正往文档里写文本的地方
        // =================================================================================

        /// <summary>
        /// 编辑会话回调。只有在这里 ec 才有效，才能对文档做写操作。
        /// 一定返回 S_OK：真正的插入结果记在 _pendingInsertHr 里，
        /// 返回失败会让 TSF 认为编辑会话本身出错（并可能反复重试）。
        /// </summary>
        public int DoEditSession(uint ec)
        {
            _pendingInsertHr = HResult.E_FAIL;
            try
            {
                string text = _pendingInsert;
                if (string.IsNullOrEmpty(text)) { return HResult.S_OK; }

                _pendingInsertHr = InsertText(ec, text);
            }
            catch (Exception ex)
            {
                _pendingInsertHr = ex.HResult != 0 ? ex.HResult : HResult.E_FAIL;
            }
            return HResult.S_OK;
        }

        /// <summary>
        /// 在编辑会话里把文本插到当前选区。
        /// 主路径用 ITfInsertAtSelection（由 TSF 管理器实现，各宿主行为一致），
        /// 带上 TF_IAS_NO_DEFAULT_COMPOSITION 表示「不要建立 composition，直接落到文档里」。
        /// 失败时退回「取选区 → ITfRange::SetText」。
        /// </summary>
        private int InsertText(uint ec, string text)
        {
            ITfContext context = _pendingContext;
            if (context == null) { return HResult.E_FAIL; }

            int hr = TryInsertAtSelection(context, ec, text);
            if (hr == HResult.S_OK) { return HResult.S_OK; }

            int hr2 = TrySetTextAtSelection(context, ec, text);
            if (hr2 == HResult.S_OK) { return HResult.S_OK; }

            // 两个都失败：把第一个的错误码带出去，便于排障。
            return hr;
        }

        private static int TryInsertAtSelection(ITfContext context, uint ec, string text)
        {
            try
            {
                ITfInsertAtSelection insert = context as ITfInsertAtSelection;
                if (insert == null) { return HResult.E_NOINTERFACE; }

                ITfRange range;
                int hr = insert.InsertTextAtSelection(
                    ec,
                    TsfFlags.TF_IAS_NO_DEFAULT_COMPOSITION,
                    text,
                    text.Length,
                    out range);

                // range 由 CLR 封送，这里不手动 Release。
                GC.KeepAlive(range);
                return hr;
            }
            catch (Exception ex)
            {
                return ex.HResult != 0 ? ex.HResult : HResult.E_FAIL;
            }
        }

        /// <summary>
        /// 后备路径：取当前选区得到一个 ITfRange，直接在它上面 SetText。
        /// TF_SELECTION 结构体手工按布局读（指针 8 字节 + TfActiveSelEnd 4 字节 + BOOL 4 字节 = 16 字节）。
        /// </summary>
        private static int TrySetTextAtSelection(ITfContext context, uint ec, string text)
        {
            const int SelectionSize = 16;   // sizeof(TF_SELECTION) on x64
            IntPtr pSelection = IntPtr.Zero;
            try
            {
                pSelection = Marshal.AllocCoTaskMem(SelectionSize);
                for (int i = 0; i < SelectionSize; i++) { Marshal.WriteByte(pSelection, i, 0); }

                uint fetched;
                int hr = context.GetSelection(ec, 0, 1, pSelection, out fetched);
                if (hr != HResult.S_OK || fetched == 0) { return hr != HResult.S_OK ? hr : HResult.E_FAIL; }

                IntPtr pRange = Marshal.ReadIntPtr(pSelection);
                if (pRange == IntPtr.Zero) { return HResult.E_FAIL; }

                // 刻意不 Marshal.Release(pRange)：
                // 若 GetObjectForIUnknown 已 AddRef，则这里是多留一个引用（进程退出前回收，无害）；
                // 若未 AddRef，则恰好由 RCW 接管这一个引用。两种情况都不会提前释放。
                ITfRange range = Marshal.GetObjectForIUnknown(pRange) as ITfRange;
                if (range == null) { return HResult.E_NOINTERFACE; }

                return range.SetText(ec, 0, text, text.Length);
            }
            catch (Exception ex)
            {
                return ex.HResult != 0 ? ex.HResult : HResult.E_FAIL;
            }
            finally
            {
                if (pSelection != IntPtr.Zero) { Marshal.FreeCoTaskMem(pSelection); }
            }
        }

        // =================================================================================
        // 输入状态机
        // =================================================================================

        private ITfContext _pendingContext;

        /// <summary>这个键要不要被我们吃掉。</summary>
        private bool ShouldEat(int vk)
        {
            if (!_active || !_chineseMode) { return false; }
            if (HasControlOrAlt()) { return false; }

            if (IsLetterKey(vk)) { return true; }

            // 只有已经进入输入状态，功能键才归我们管；
            // 否则空格/回车/退格必须是宿主的正常编辑键。
            if (_buffer.Length == 0) { return false; }

            switch (vk)
            {
                case VK_BACK:
                case VK_RETURN:
                case VK_SPACE:
                case VK_ESCAPE:
                case VK_UP:
                case VK_DOWN:
                case VK_PRIOR:
                case VK_NEXT:
                case VK_HOME:
                case VK_END:
                    return true;
                default:
                    return vk >= 0x31 && vk <= 0x39;
            }
        }

        private void AppendLetter(char c, ITfContext pic)
        {
            // 词典只加载成功才可能走到这里（ShouldEat 里已经确保），但防御一下。
            if (EnsureEngine() == null) { return; }

            _buffer.Append(c);
            _selectedIndex = 0;
            RefreshCandidates();
            UpdatePanel(pic);
        }

        private void Backspace(ITfContext pic)
        {
            if (_buffer.Length == 0) { return; }

            _buffer.Length = _buffer.Length - 1;
            _selectedIndex = 0;

            if (_buffer.Length == 0)
            {
                _candidates = null;
                HidePanel();
                return;
            }

            RefreshCandidates();
            UpdatePanel(pic);
        }

        private void SelectByDigit(int digit, ITfContext pic)
        {
            if (_candidates == null || digit < 1 || digit > _candidates.Count) { return; }
            _selectedIndex = digit - 1;
            CommitSelected(pic);
        }

        private void MoveSelection(int delta, ITfContext pic)
        {
            if (_candidates == null || _candidates.Count == 0) { return; }

            int index = _selectedIndex + delta;
            if (index < 0) { index = 0; }
            if (index >= _candidates.Count) { index = _candidates.Count - 1; }
            if (index == _selectedIndex) { return; }

            _selectedIndex = index;
            UpdatePanel(pic);
        }

        /// <summary>提交当前选中的候选（空格 / 回车 / 数字键）。</summary>
        private void CommitSelected(ITfContext pic)
        {
            string text = GetTextToCommit();
            if (string.IsNullOrEmpty(text)) { ResetInput(); HidePanel(); return; }

            if (InsertIntoDocument(pic, text))
            {
                ResetInput();
                HidePanel();
            }
            else
            {
                // 插入失败（选区被锁、宿主不支持…）：保留缓冲让用户还能按 Esc 退出或重试。
                UpdatePanel(pic);
            }
        }

        /// <summary>要被写进文档的最终文本 —— 已按 IDS 拆解过的形体串。</summary>
        private string GetTextToCommit()
        {
            if (_candidates != null && _candidates.Count > 0)
            {
                int index = _selectedIndex;
                if (index < 0) { index = 0; }
                if (index >= _candidates.Count) { index = _candidates.Count - 1; }
                return _candidates[index].Transformed;
            }

            // 没有候选（词典里查不到）：按原样提交用户敲进去的字母，至少不丢输入。
            if (_buffer.Length > 0) { return _buffer.ToString(); }
            return null;
        }

        private void RefreshCandidates()
        {
            XingbianEngine engine = EnsureEngine();
            if (engine == null || _buffer.Length == 0)
            {
                _candidates = null;
                return;
            }

            try
            {
                _candidates = engine.GetCandidates(_buffer.ToString(), MaxCandidates);
            }
            catch (Exception)
            {
                _candidates = null;
            }
        }

        private void ResetInput()
        {
            _buffer.Length = 0;
            _candidates = null;
            _selectedIndex = 0;
            _pendingContext = null;
            _shiftPending = false;
            _shiftChord = false;
        }

        // =================================================================================
        // 提交
        // =================================================================================

        /// <summary>
        /// 同步地跑一次编辑会话把文本插进去。
        /// 用 TF_ES_SYNC：按键处理本来就在 UI 线程，同步返回最简单，也避免异步会话
        /// 在我们已经改了 _buffer 之后才执行（那会提交错内容）。
        /// </summary>
        private bool InsertIntoDocument(ITfContext pic, string text)
        {
            if (pic == null) { return false; }

            _pendingContext = pic;
            _pendingInsert = text;
            _pendingInsertHr = HResult.E_FAIL;

            int hrSession;
            int hr = pic.RequestEditSession(
                _tid,
                this,
                TsfFlags.TF_ES_SYNC | TsfFlags.TF_ES_READWRITE,
                out hrSession);

            _pendingInsert = null;
            _pendingContext = null;

            if (hr != HResult.S_OK) { return false; }
            return _pendingInsertHr == HResult.S_OK;
        }

        // =================================================================================
        // 候选浮窗
        // =================================================================================

        private CandidatePanel Panel
        {
            get
            {
                if (_panel == null)
                {
                    try
                    {
                        CandidatePanel panel = new CandidatePanel();
                        // 在 TSF 激活时（此处必然在宿主 UI 线程上）就把窗口建好：
                        // 面板的 Show 只允许在创建窗口的线程上真正建窗口，提前建可避免
                        // 后续从别的线程 Show 时被拒绝。
                        panel.EnsureCreated();

                        // 鼠标点选候选。事件由面板在它自己的窗口过程里触发，
                        // 而那个窗口就建在当前线程上，所以这里天然是 UI 线程。
                        panel.ItemClicked += OnPanelItemClicked;
                        _panelClickHooked = true;

                        _panel = panel;
                    }
                    catch (Exception)
                    {
                        _panel = null;
                        _panelClickHooked = false;
                    }
                }
                return _panel;
            }
        }

        /// <summary>
        /// 用户在候选浮窗上点了第 index 行 → 选中并提交。
        /// 这条路径与键盘选候选走同一个 CommitSelected，行为一致。
        /// </summary>
        private void OnPanelItemClicked(object sender, int index)
        {
            try
            {
                // 面板的 Show 只从本线程发起，它的窗口过程也就在本线程；
                // 万一将来有人从别的线程 Show 而让事件落到别处，这里直接放弃，
                // 绝不在非 UI 线程上碰 _buffer / _selectedIndex。
                if (Thread.CurrentThread.ManagedThreadId != (int)_uiThreadId) { return; }

                if (_candidates == null || index < 0 || index >= _candidates.Count) { return; }

                // 提交需要一个 ITfContext。鼠标点击不携带上下文，用当前焦点文档管理器的顶层上下文。
                ITfContext pic = GetCurrentContext();
                if (pic == null) { return; }

                _selectedIndex = index;
                CommitSelected(pic);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 取当前焦点文档的顶层上下文。鼠标点选时没有现成的 pic 参数，用它来补。
        /// 取不到就返回 null（调用方放弃本次操作，不影响宿主）。
        /// </summary>
        private ITfContext GetCurrentContext()
        {
            try
            {
                if (_threadMgr == null) { return null; }

                ITfDocumentMgr docMgr;
                if (_threadMgr.GetFocus(out docMgr) != HResult.S_OK || docMgr == null) { return null; }

                try
                {
                    ITfContext context;
                    if (docMgr.GetTop(out context) == HResult.S_OK) { return context; }
                }
                finally
                {
                    // docMgr 是 RCW，交给 GC 回收即可；这里显式释放会破坏别处持有的引用计数。
                }
            }
            catch (Exception) { }
            return null;
        }

        private void DestroyPanel()
        {
            try
            {
                if (_panel != null)
                {
                    if (_panelClickHooked) { _panel.ItemClicked -= OnPanelItemClicked; }
                    _panel.Dispose();
                }
            }
            catch (Exception) { }
            _panel = null;
            _panelClickHooked = false;
        }

        private void HidePanel()
        {
            try
            {
                if (_panel != null) { _panel.Hide(); }
            }
            catch (Exception) { }
        }

        private void UpdatePanel(ITfContext pic)
        {
            try
            {
                CandidatePanel panel = Panel;
                if (panel == null) { return; }

                if (_buffer.Length == 0) { panel.Hide(); return; }

                int x;
                int y;
                if (!TryGetCaretScreenPosition(out x, out y)) { return; }

                string[] labels;
                string[] notes;

                if (_candidates != null && _candidates.Count > 0)
                {
                    labels = new string[_candidates.Count];
                    notes = new string[_candidates.Count];
                    for (int i = 0; i < _candidates.Count; i++)
                    {
                        Candidate candidate = _candidates[i];
                        // 左栏 = 已形变文本（亻尔 女马），右栏 = 原词（你妈）
                        labels[i] = candidate.Transformed;
                        notes[i] = candidate.Word;
                    }
                }
                else
                {
                    // 一个候选都没有时也要把浮窗显示出来（否则用户敲了键屏幕上毫无反应）。
                    labels = new string[1];
                    notes = new string[1];
                    labels[0] = _buffer.ToString();
                    notes[0] = EnsureEngine() != null
                        ? "无匹配候选 · 空格按原样输入"
                        : "词典未加载";
                }

                // 这个重载会在面板顶部多画一行状态行，用来显示拼音缓存与「第几/共几」。
                // 选中项越界时交给面板处理（面板会自行钳制）。
                panel.Show(x, y, BuildStatusText(), labels, notes, _selectedIndex);
            }
            catch (Exception) { }
        }

        /// <summary>浮窗顶部那行状态文字（拼音缓存 + 候选位置）。</summary>
        private string BuildStatusText()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(_buffer.ToString());

            if (_candidates != null && _candidates.Count > 0)
            {
                sb.Append("   ");
                sb.Append((_selectedIndex + 1).ToString());
                sb.Append('/');
                sb.Append(_candidates.Count.ToString());
            }
            else
            {
                sb.Append("   (无候选)");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 取插入符在屏幕上的位置，作为浮窗锚点。
        /// 用 GetGUIThreadInfo 拿本线程的插入符矩形（比封送 TF_SELECTION 安全得多）。
        /// </summary>
        private static bool TryGetCaretScreenPosition(out int x, out int y)
        {
            x = 0;
            y = 0;

            try
            {
                GUITHREADINFO info = new GUITHREADINFO();
                info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));

                if (TsfNative.GetGUIThreadInfo(0, ref info))
                {
                    if (info.hwndCaret != IntPtr.Zero)
                    {
                        POINT pt = new POINT(info.rcCaret.left, info.rcCaret.bottom);
                        if (TsfNative.ClientToScreen(info.hwndCaret, ref pt))
                        {
                            x = pt.x;
                            y = pt.y;
                            return true;
                        }
                    }

                    if (info.hwndFocus != IntPtr.Zero)
                    {
                        RECT rc;
                        if (TsfNative.GetWindowRect(info.hwndFocus, out rc))
                        {
                            x = rc.left + 12;
                            y = rc.top + 12;
                            return true;
                        }
                    }
                }

                POINT cursor;
                if (TsfNative.GetCursorPos(out cursor))
                {
                    x = cursor.x;
                    y = cursor.y;
                    return true;
                }
            }
            catch (Exception) { }

            return false;
        }

        // =================================================================================
        // 小工具
        // =================================================================================

        private static int GetVirtualKey(IntPtr wParam)
        {
            return unchecked((int)(wParam.ToInt64() & 0xFFFF));
        }

        private static bool IsLetterKey(int vk)
        {
            return vk >= 0x41 && vk <= 0x5A;
        }

        private static char LetterFromKey(int vk)
        {
            return (char)('a' + (vk - 0x41));
        }

        /// <summary>Ctrl / Alt 按下时不归输入法管，必须让宿主拿到（否则 Ctrl+C 之类全废）。</summary>
        private static bool HasControlOrAlt()
        {
            return IsKeyDown(VK_CONTROL) || IsKeyDown(VK_MENU);
        }

        private static bool IsKeyDown(int vk)
        {
            return (GetKeyState(vk) & 0x8000) != 0;
        }

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
    }
}
