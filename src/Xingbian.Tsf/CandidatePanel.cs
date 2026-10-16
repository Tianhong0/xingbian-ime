// ============================================================================
//  CandidatePanel.cs
//  —— 候选词面板：纯 Win32 / GDI 自绘的无焦点浮窗
//
//  为什么不用 WinForms / WPF：
//    本 DLL 是被宿主（记事本 / 浏览器 / Office）**进程内加载**的 TSF 文本服务。
//    WinForms/WPF 会引入自己的消息循环与大量依赖，和宿主已有的循环冲突，
//    而且一旦激活就会把焦点从宿主抢走 —— 输入直接断掉。
//    所以这里只有 CreateWindowExW + WndProc + GDI，消息由宿主自己的循环泵派发。
//
//  与 CandidatePanel.Native.cs 的分工：
//    本文件 = 生命周期 / DPI / 布局 / 测量 / 定位 / 绘制 / 命中测试
//    CandidatePanel.Native.cs = P/Invoke 声明 + 原生结构体 + 常量
//
//  三条硬约束（违反任何一条都会在宿主进程里出事）：
//    1. 绝不抢焦点：WS_EX_NOACTIVATE + SW_SHOWNOACTIVATE +
//       SetWindowPos(SWP_NOACTIVATE|SWP_NOZORDER) + WM_MOUSEACTIVATE 返回 MA_NOACTIVATE。
//       全程不调用 SetForegroundWindow / SetFocus。
//    2. 绝不抛异常进宿主：所有公开 API 与 WndProc 都吞掉异常。
//       宿主是记事本，一个未捕获异常就是记事本崩溃。
//    3. 绝不自建消息循环：没有 GetMessage / Application.Run。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Xingbian.Tsf
{
    /// <summary>
    /// 候选词面板。纯 Win32 顶层 popup 窗口，不抢焦点、不进任务栏。
    /// </summary>
    internal sealed class CandidatePanel : IDisposable
    {
        // ====================================================================
        //  可调常量
        // ====================================================================

        /// <summary>窗口类名。进程内唯一，只需注册一次。</summary>
        private const string WINDOW_CLASS_NAME = "XingbianIME.CandidatePanel.Window";

        /// <summary>窗口标题。面板不显示标题栏，仅用于调试时辨认。</summary>
        private const string WINDOW_TITLE = "Xingbian IME Candidate Panel";

        // ---- 字号（@96dpi 的逻辑像素高度；负数 = 字符高度而非单元格高度）----
        /// <summary>主文本（候选词，已形变）@96dpi。</summary>
        private const int FONT_PX_MAIN = 20;
        /// <summary>次要文本（说明 / 序号）@96dpi。</summary>
        private const int FONT_PX_SMALL = 13;

        // ---- 字体族候选（按顺序探测第一个真实存在的）------------------------
        //   形变结果是「亻尔」这类部首拼接，字体必须能覆盖 CJK 部首扩展区，
        //   否则就是豆腐块。雅黑系覆盖最全，SimSun 兜底。
        private static readonly string[] FontFaceCandidates = new string[]
        {
            "Microsoft YaHei UI",
            "Microsoft YaHei",
            "SimSun"
        };

        // ---- 布局（@96dpi 基准，使用前一律 Scale()）-------------------------
        private const int BASE_PAD_X = 9;        // 面板左右内边距
        private const int BASE_PAD_Y = 4;        // 每行上下内边距
        private const int BASE_COL_GAP = 10;     // 各列之间的横向间距
        private const int BASE_MIN_WIDTH = 88;   // 面板最小宽度
        private const int BASE_GAP_ANCHOR = 2;   // 面板与锚点之间的缝隙
        private const int BASE_STATUS_PAD_Y = 3; // 状态行上下内边距

        /// <summary>宽度上限 = 工作区宽度 × 此比例。</summary>
        private const double MAX_WIDTH_RATIO = 0.8;
        /// <summary>高度上限 = 工作区高度 × 此比例。</summary>
        private const double MAX_HEIGHT_RATIO = 0.5;

        // ---- 高亮行的悬停底色混合比例（把高亮色往窗口底色方向拉多少）--------
        private const double HOVER_BLEND = 0.72;

        // ====================================================================
        //  静态状态（全进程共享）
        // ====================================================================

        private static readonly object s_classLock = new object();
        private static bool s_classRegistered;

        /// <summary>
        /// ⚠ 窗口过程委托必须由静态字段常驻引用。
        /// 若只挂在局部变量上，GC 会回收它的封送跳板，之后再收到窗口消息就是野指针
        /// → 宿主进程直接崩。这是 Win32 P/Invoke 最常见的致命坑。
        /// </summary>
        private static readonly PanelNative.WndProcDelegate s_wndProc = WndProcImpl;

        // ====================================================================
        //  实例状态
        // ====================================================================

        /// <summary>
        /// 创建窗口的那个线程。构造时先记成当前线程，窗口一旦真的建出来就改成
        /// 创建它的线程 —— HWND 归创建者所有，跨线程操作会得到不一致状态。
        /// </summary>
        private int _uiThreadId;

        private IntPtr _hwnd = IntPtr.Zero;
        private GCHandle _selfHandle;
        private bool _selfHandleAllocated;
        private bool _disposed;
        private bool _visible;
        private int _lastError;

        /// <summary>当前显示内容。面板绘制期间只读。</summary>
        private PanelItem[] _items = new PanelItem[0];
        private int _selectedIndex = -1;

        /// <summary>顶部的状态行文本（拼音缓存 + "1/5" 之类）；null = 不显示状态行。</summary>
        private string _statusText;

        // ---- GDI 对象缓存（Dispose 时统一 DeleteObject）----------------------
        private IntPtr _hFontMain = IntPtr.Zero;
        private IntPtr _hFontSmall = IntPtr.Zero;
        private IntPtr _hbrBack = IntPtr.Zero;      // COLOR_WINDOW
        private IntPtr _hbrText = IntPtr.Zero;      // COLOR_WINDOWTEXT（未用，见 DrawRow 注释）
        private IntPtr _hbrSel = IntPtr.Zero;       // COLOR_HIGHLIGHT
        private IntPtr _hbrHover = IntPtr.Zero;     // 高亮色与底色混合
        private IntPtr _hbrBorder = IntPtr.Zero;    // COLOR_3DSHADOW

        // ---- 双缓冲（按客户区尺寸惰性重建）----------------------------------
        private IntPtr _hMemDc = IntPtr.Zero;
        private IntPtr _hMemBmp = IntPtr.Zero;
        private IntPtr _hMemBmpOld = IntPtr.Zero;
        private int _memWidth;
        private int _memHeight;

        // ---- DPI 与度量缓存 --------------------------------------------------
        private int _dpi = 96;
        private int _fontDpi = -1;              // 字体/画刷按哪个 DPI 建的；-1 = 未建
        private int _mainLineHeight = FONT_PX_MAIN;
        private int _smallLineHeight = FONT_PX_SMALL;
        private string _fontFaceName;           // 实际兑现的字体族名（诊断用）

        // ---- 每次 Show 算出的绘制参数（避免 WM_PAINT 里重复测量）------------
        private int _padX;
        private int _padY;
        private int _colGap;
        private int _border;
        private int _rowHeight;
        private int _rowsTop;
        private int _numWidth;
        private int _dispWidth;
        private int _noteWidth;
        private int _panelWidth;
        private int _statusHeight;   // 状态行高度（含分隔线）；0 = 无状态行
        private int _statusWidth;    // 状态行文本宽度（测量用）

        // ---- 鼠标悬停行（仅用于视觉反馈）------------------------------------
        private int _hoverIndex = -1;
        private bool _trackingMouse;

        // ====================================================================
        //  公开 API（签名冻结，勿改）
        // ====================================================================

        /// <summary>
        /// 创建面板对象。此时**不**创建窗口（惰性），不会触碰宿主 UI，
        /// 也不会往宿主消息队列里塞任何东西。
        /// </summary>
        public CandidatePanel()
        {
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>窗口句柄；窗口尚未创建或已销毁时为 <see cref="IntPtr.Zero"/>。</summary>
        public IntPtr Handle
        {
            get { return _hwnd; }
        }

        /// <summary>面板当前是否真的可见（同时校验 HWND 仍然有效）。</summary>
        public bool IsVisible
        {
            get
            {
                if (!_visible || _hwnd == IntPtr.Zero) return false;
                try
                {
                    return PanelNative.IsWindow(_hwnd) && PanelNative.IsWindowVisible(_hwnd);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>用户用鼠标点选了第几项（0 基，对应当前显示的列表）。</summary>
        public event EventHandler<int> ItemClicked;

        /// <summary>最近一次原生调用失败的错误码；0 = 没记录到错误。仅用于诊断。</summary>
        public int LastError
        {
            get { return _lastError; }
        }

        /// <summary>
        /// 当前实际使用的字体族名（诊断用）；字体尚未创建时为 null。
        /// 用来确认「Microsoft YaHei UI」到底有没有被系统兑现，还是退化成了别的字体。
        /// </summary>
        internal string FontFaceName
        {
            get { return _fontFaceName; }
        }

        /// <summary>
        /// 在**当前线程**上提前创建窗口类与窗口。
        /// 建议宿主在 TSF 激活时（此时一定在 UI 线程上）调一次，
        /// 这样后续即使用别的线程 Show 也能靠 PostMessage 切回来。
        /// 失败不抛异常。
        /// </summary>
        public void EnsureCreated()
        {
            if (_disposed) return;
            try
            {
                if (!EnsureClassRegistered()) return;
                EnsureWindow();
            }
            catch (Exception ex)
            {
                _lastError = ex.HResult;
            }
        }

        /// <summary>
        /// 幂等地注册窗口类（不创建窗口）。供宿主在 TSF 激活时（一定在 UI 线程上）
        /// 提前调用，避免之后在非 UI 线程上首次建窗口。
        /// 类已存在（ERROR_CLASS_ALREADY_EXISTS）视为成功；任何失败都不抛异常。
        /// </summary>
        public void RegisterWindowClass()
        {
            if (_disposed) return;
            try
            {
                EnsureClassRegistered();
            }
            catch (Exception ex)
            {
                _lastError = ex.HResult;
            }
        }

        /// <summary>
        /// 显示 / 更新面板。
        /// </summary>
        /// <param name="anchorScreen">
        /// 锚点矩形（屏幕坐标），通常是插入符位置。面板对齐到它下方，放不下则翻到上方。
        /// </param>
        /// <param name="items">候选列表；null 或空 → 等同 <see cref="Hide"/>。</param>
        /// <param name="selectedIndex">当前高亮项下标（0 基）；越界时钳制到合法范围。</param>
        public void Show(PanelRect anchorScreen, IList<PanelItem> items, int selectedIndex)
        {
            if (_disposed) return;

            // 空列表 = 没有候选 → 收起面板，而不是显示一个空壳
            if (items == null || items.Count == 0)
            {
                Hide();
                return;
            }

            // 快照成数组：PanelItem 不可变，跨线程读取安全，且不受调用方后续修改影响
            PanelItem[] snapshot = new PanelItem[items.Count];
            for (int i = 0; i < snapshot.Length; i++) snapshot[i] = items[i];

            // statusText 传 null：本重载没有状态行参数，顺带把上次遗留的状态行清掉，
            // 免得混用两个 Show 重载后顶部残留一行旧文字。
            ShowInternal(anchorScreen, snapshot, selectedIndex, null);
        }

        /// <summary>两个公开 Show 重载的共同实现。仅在需要时改 statusText。</summary>
        private void ShowInternal(PanelRect anchorScreen, PanelItem[] snapshot, int selectedIndex, string statusText)
        {
            if (IsOnUiThread())
            {
                _statusText = statusText;
                ShowCore(anchorScreen, snapshot, selectedIndex);
                return;
            }

            // ---- 非 UI 线程：把请求投递回创建窗口的线程 ----------------------
            // 不能在这里直接建窗口 / 调 SetWindowPos：窗口属于创建它的线程，
            // 从别的线程操作会得到不一致的状态，而且新线程没有消息泵。
            IntPtr hwnd = _hwnd;
            if (hwnd == IntPtr.Zero || !SafeIsWindow(hwnd))
            {
                // 窗口还没建，而这里又不是 UI 线程 —— 无法安全创建。
                // 记录错误后放弃（绝不崩）。宿主应先调 EnsureCreated()。
                _lastError = ERROR_NOT_ON_UI_THREAD;
                return;
            }

            ShowRequest req = new ShowRequest();
            req.Anchor = anchorScreen;
            req.Items = snapshot;
            req.SelectedIndex = selectedIndex;
            req.StatusText = statusText;

            PostRequest(hwnd, PanelNative.WM_XB_SHOW, req);
        }

        // --------------------------------------------------------------------
        //  兼容重载：给"只有插入符点 + 字符串数组"的调用方用。
        //  冻结契约里的 Show(PanelRect,...) 才是主入口，这两个只是薄封装：
        //    · 把 (x, y) 点展开成 1px 宽的锚点矩形（插入符就是这么窄）
        //    · 把 labels/notes 两个平行数组拼成 PanelItem[]
        //  注意：statusText 版本会额外画一行状态栏，非 null 时占用顶部空间。
        // --------------------------------------------------------------------

        /// <summary>
        /// 用插入符屏幕坐标 + 平行数组显示面板（无状态行）。
        /// </summary>
        /// <param name="screenX">插入符左下角 X（屏幕坐标）。</param>
        /// <param name="screenY">插入符左下角 Y（屏幕坐标）。</param>
        /// <param name="labels">主显示文本（已形变），与 notes 按下标对应。</param>
        /// <param name="notes">次要说明；可为 null 或更短的数组（缺失项按空处理）。</param>
        /// <param name="selectedIndex">当前高亮项下标（0 基）；越界由面板钳制。</param>
        public void Show(int screenX, int screenY, string[] labels, string[] notes, int selectedIndex)
        {
            Show(screenX, screenY, null, labels, notes, selectedIndex);
        }

        /// <summary>
        /// 用插入符屏幕坐标 + 平行数组显示面板，并可选在顶部显示一行状态文本
        /// （拼音缓存 + "1/5" 之类）。
        /// </summary>
        /// <param name="screenX">插入符左下角 X（屏幕坐标）。</param>
        /// <param name="screenY">插入符左下角 Y（屏幕坐标）。</param>
        /// <param name="statusText">顶部状态行；null 或空串 = 不显示状态行。</param>
        /// <param name="labels">主显示文本（已形变）。</param>
        /// <param name="notes">次要说明；可为 null 或更短。</param>
        /// <param name="selectedIndex">当前高亮项下标（0 基）。</param>
        public void Show(int screenX, int screenY, string statusText, string[] labels, string[] notes, int selectedIndex)
        {
            if (_disposed) return;

            if (labels == null || labels.Length == 0)
            {
                Hide();
                return;
            }

            // 平行数组 → PanelItem[]（notes 短了或为 null 都按空说明处理）
            PanelItem[] items = new PanelItem[labels.Length];
            for (int i = 0; i < items.Length; i++)
            {
                string note = (notes != null && i < notes.Length) ? notes[i] : null;
                items[i] = new PanelItem(labels[i], note);
            }

            _statusText = string.IsNullOrEmpty(statusText) ? null : statusText;

            // 插入符是一个 1px 宽的竖条；把它当作锚点矩形即可复用同一套定位逻辑
            // 走 ShowInternal 而不是公开 Show，避免把刚设好的状态行又清掉
            ShowInternal(new PanelRect(screenX, screenY, 1, 1), items, selectedIndex, _statusText);
        }

        /// <summary>隐藏面板。未显示时是空操作。幂等。</summary>
        public void Hide()
        {
            if (_disposed) return;

            try
            {
                if (IsOnUiThread())
                {
                    HideCore();
                    return;
                }

                IntPtr hwnd = _hwnd;
                if (hwnd != IntPtr.Zero && SafeIsWindow(hwnd))
                {
                    PanelNative.PostMessageW(hwnd, PanelNative.WM_XB_HIDE, IntPtr.Zero, IntPtr.Zero);
                }
                _visible = false;   // 立刻对外反映"逻辑上已隐藏"
            }
            catch (Exception ex)
            {
                _lastError = ex.HResult;
            }
        }

        /// <summary>
        /// 销毁窗口并释放全部 GDI 对象。幂等，可重复调用。
        /// 若在非 UI 线程调用，则把销毁动作投递回 UI 线程（避免跨线程销毁窗口）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                IntPtr hwnd = _hwnd;
                if (hwnd != IntPtr.Zero && SafeIsWindow(hwnd) && !IsOnUiThread())
                {
                    // 跨线程：交给 UI 线程做 DestroyWindow，GDI 也在那边释放
                    PanelNative.PostMessageW(hwnd, PanelNative.WM_XB_DESTROY, IntPtr.Zero, IntPtr.Zero);
                    _visible = false;
                    return;
                }
            }
            catch (Exception)
            {
                // 落到下面直接清理
            }

            ReleaseEverything();
        }

        // ====================================================================
        //  核心实现（仅在 UI 线程上执行）
        // ====================================================================

        private void ShowCore(PanelRect anchorScreen, PanelItem[] items, int selectedIndex)
        {
            try
            {
                if (!EnsureClassRegistered()) return;
                if (!EnsureWindow()) return;

                // 1) 采样 DPI 与系统配色（都必须在建窗口之后：GetDpiForWindow 需要 HWND）
                EnsureDpi();
                EnsureGdiResources();
                EnsureFonts();

                // 2) 取锚点所在显示器的工作区 —— 尺寸上限与边界钳制都依赖它
                PanelNative.RECT work = GetWorkArea(anchorScreen);

                // 3) 测量 + 布局
                int panelW;
                int panelH;
                MeasureLayout(items, work, out panelW, out panelH);

                // 4) 定位（下方 → 上方翻转 → 工作区钳制）
                int x;
                int y;
                ComputePosition(anchorScreen, work, panelW, panelH, out x, out y);

                // 5) 状态就位后才重绘，避免画出半新半旧的帧
                _items = items;
                _selectedIndex = ClampIndex(selectedIndex, items.Length);
                _hoverIndex = -1;
                _panelWidth = panelW;

                // 尺寸变了 → 内存位图要跟着重建
                if (_memWidth != panelW || _memHeight != panelH) ReleaseDoubleBuffer();

                PanelNative.SetWindowPos(
                    _hwnd, IntPtr.Zero, x, y, panelW, panelH,
                    PanelNative.SWP_NOACTIVATE | PanelNative.SWP_NOZORDER);

                PanelNative.InvalidateRect(_hwnd, IntPtr.Zero, false);
                PanelNative.ShowWindow(_hwnd, PanelNative.SW_SHOWNOACTIVATE);
                PanelNative.UpdateWindow(_hwnd);

                _visible = true;
                _lastError = 0;
            }
            catch (Exception ex)
            {
                // 宿主进程里抛异常 = 记事本崩溃。一律吞掉并降级为"不显示"。
                _lastError = ex.HResult;
            }
        }

        private void HideCore()
        {
            try
            {
                if (_hwnd != IntPtr.Zero && SafeIsWindow(_hwnd))
                {
                    PanelNative.ShowWindow(_hwnd, PanelNative.SW_HIDE);
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.HResult;
            }
            finally
            {
                _visible = false;
                _hoverIndex = -1;
                _trackingMouse = false;
            }
        }

        /// <summary>销毁窗口 + 释放 GDI。只在 UI 线程（或窗口不存在时）调用。</summary>
        private void ReleaseEverything()
        {
            try
            {
                IntPtr hwnd = _hwnd;
                if (hwnd != IntPtr.Zero)
                {
                    if (SafeIsWindow(hwnd))
                    {
                        try { PanelNative.DestroyWindow(hwnd); }
                        catch (Exception) { }
                        // WM_NCDESTROY 正常会把 _hwnd 归零并解掉 GCHandle；
                        // 万一没收到（窗口已被系统销毁），这里兜底。
                        _hwnd = IntPtr.Zero;
                        FreeSelfHandle();
                    }
                    else
                    {
                        _hwnd = IntPtr.Zero;
                        FreeSelfHandle();
                    }
                }
                _visible = false;

                ReleaseDoubleBuffer();
                ReleaseGdiResources();
            }
            catch (Exception)
            {
                // Dispose 绝不抛
            }
        }

        private bool IsOnUiThread()
        {
            return Thread.CurrentThread.ManagedThreadId == _uiThreadId;
        }

        /// <summary>把请求对象装箱进 GCHandle 后 PostMessage；投递失败立即回收，避免泄漏。</summary>
        private void PostRequest(IntPtr hwnd, uint msg, ShowRequest req)
        {
            GCHandle handle = GCHandle.Alloc(req, GCHandleType.Normal);
            try
            {
                if (!PanelNative.PostMessageW(hwnd, msg, GCHandle.ToIntPtr(handle), IntPtr.Zero))
                {
                    _lastError = Marshal.GetLastWin32Error();
                    handle.Free();
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.HResult;
                if (handle.IsAllocated) handle.Free();
            }
        }

        // ====================================================================
        //  窗口类 / 窗口生命周期
        // ====================================================================

        /// <summary>幂等注册窗口类。返回 true 表示类可用。</summary>
        private bool EnsureClassRegistered()
        {
            if (s_classRegistered) return true;

            lock (s_classLock)
            {
                if (s_classRegistered) return true;

                try
                {
                    IntPtr hInstance = PanelNative.GetModuleHandleW(null);

                    PanelNative.WNDCLASSEXW wc = new PanelNative.WNDCLASSEXW();
                    wc.cbSize = (uint)Marshal.SizeOf(typeof(PanelNative.WNDCLASSEXW));
                    wc.style = 0;
                    wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc);
                    wc.cbClsExtra = 0;
                    wc.cbWndExtra = 0;
                    wc.hInstance = hInstance;
                    wc.hIcon = IntPtr.Zero;

                    // 箭头光标：面板是可点击的（点选候选），光标应当正常
                    IntPtr cursor = IntPtr.Zero;
                    try { cursor = PanelNative.LoadCursorW(IntPtr.Zero, PanelNative.IDC_ARROW); }
                    catch (Exception) { }
                    wc.hCursor = cursor;

                    // ⚠ 必须 NULL：背景由 WM_PAINT 自己一次性擦，交给系统擦会闪
                    wc.hbrBackground = IntPtr.Zero;
                    wc.lpszMenuName = null;
                    wc.lpszClassName = WINDOW_CLASS_NAME;
                    wc.hIconSm = IntPtr.Zero;

                    ushort atom = PanelNative.RegisterClassExW(ref wc);
                    if (atom == 0)
                    {
                        int err = Marshal.GetLastWin32Error();
                        // 1410 ERROR_CLASS_ALREADY_EXISTS：类已注册，这对我们完全等价于成功
                        // （同一个类名可能被本进程的另一个实例/AppDomain 先注册了）
                        if (err == PanelNative.ERROR_CLASS_ALREADY_EXISTS)
                        {
                            s_classRegistered = true;
                            return true;
                        }
                        _lastError = err;
                        return false;
                    }

                    s_classRegistered = true;
                    // 类注册表持有的是委托跳板的裸指针，别让 GC 收走它
                    GC.KeepAlive(s_wndProc);
                    return true;
                }
                catch (Exception ex)
                {
                    _lastError = ex.HResult;
                    return false;
                }
            }
        }

        /// <summary>惰性创建窗口。失败不抛异常，返回 false（Show 退化为空操作）。</summary>
        private bool EnsureWindow()
        {
            if (_hwnd != IntPtr.Zero)
            {
                if (SafeIsWindow(_hwnd)) return true;
                // 窗口在我们背后被销毁了（宿主退出等）→ 清干净重来
                FreeSelfHandle();
                _hwnd = IntPtr.Zero;
                _visible = false;
            }

            int exStyle = PanelNative.WS_EX_TOOLWINDOW    // 不进 Alt+Tab / 任务栏
                        | PanelNative.WS_EX_TOPMOST       // 永远浮在普通窗口之上
                        | PanelNative.WS_EX_NOACTIVATE;   // 关键：绝不抢焦点

            IntPtr hwnd;
            try
            {
                hwnd = PanelNative.CreateWindowExW(
                    (uint)exStyle,
                    WINDOW_CLASS_NAME,
                    WINDOW_TITLE,
                    PanelNative.WS_POPUP,
                    0, 0, BASE_MIN_WIDTH, 40,     // 初始尺寸；Show 时再精确定位
                    IntPtr.Zero, IntPtr.Zero,
                    PanelNative.GetModuleHandleW(null),
                    IntPtr.Zero);
            }
            catch (Exception ex)
            {
                _lastError = ex.HResult;
                return false;
            }

            if (hwnd == IntPtr.Zero)
            {
                _lastError = Marshal.GetLastWin32Error();
                return false;
            }

            _hwnd = hwnd;

            // HWND 归创建它的线程所有 —— 从这里开始，跨线程判定一律以该线程为准
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;

            // 建立 HWND → 实例 的映射，供静态 WndProc 派发消息
            try
            {
                _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
                _selfHandleAllocated = true;
                PanelNative.SetWindowLongPtrW(hwnd, PanelNative.GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));
            }
            catch (Exception ex)
            {
                _lastError = ex.HResult;
                FreeSelfHandle();
                // 没有实例映射的窗口收不到我们的绘制逻辑，留着也没用
                try { PanelNative.DestroyWindow(hwnd); }
                catch (Exception) { }
                _hwnd = IntPtr.Zero;
                return false;
            }

            return true;
        }

        private void FreeSelfHandle()
        {
            if (_selfHandleAllocated)
            {
                try
                {
                    if (_selfHandle.IsAllocated) _selfHandle.Free();
                }
                catch (Exception) { }
                _selfHandleAllocated = false;
            }
        }

        private static bool SafeIsWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return PanelNative.IsWindow(hwnd); }
            catch (Exception) { return false; }
        }

        // ====================================================================
        //  DPI
        // ====================================================================

        /// <summary>
        /// 采样 DPI：GetDpiForWindow（Win10 1607+，动态解析）
        ///   → GetDeviceCaps(hdc, LOGPIXELSY)（老系统 / 导出缺失）
        ///   → 96（最终兜底）。
        /// </summary>
        private void EnsureDpi()
        {
            int dpi = 0;

            // 首选：按窗口所在显示器取 DPI（多显示器混合 DPI 下这才是正确的那个）
            try
            {
                if (PanelNative.TryGetDpiForWindow(_hwnd, out dpi)) { }
            }
            catch (Exception)
            {
                dpi = 0;
            }

            // 回退：主显示器的垂直 DPI
            if (dpi < 48 || dpi > 480)
            {
                IntPtr hdc = IntPtr.Zero;
                try
                {
                    hdc = PanelNative.GetDC(IntPtr.Zero);
                    if (hdc != IntPtr.Zero) dpi = PanelNative.GetDeviceCaps(hdc, PanelNative.LOGPIXELSY);
                }
                catch (Exception)
                {
                    dpi = 0;
                }
                finally
                {
                    if (hdc != IntPtr.Zero)
                    {
                        try { PanelNative.ReleaseDC(IntPtr.Zero, hdc); }
                        catch (Exception) { }
                    }
                }
            }

            // 最终兜底：宁可画得偏小，也不要拿一个离谱的 DPI 去乘
            if (dpi < 48 || dpi > 480) dpi = 96;

            _dpi = dpi;
        }

        /// <summary>把 @96dpi 的设计值按当前 DPI 缩放（四舍五入，非零基准至少 1px）。</summary>
        private int Scale(int baseValueAt96Dpi)
        {
            if (baseValueAt96Dpi == 0) return 0;
            int v = PanelNative.MulDiv(baseValueAt96Dpi, _dpi, 96);
            return v < 1 ? 1 : v;
        }

        // ====================================================================
        //  GDI 资源：字体 / 画刷
        // ====================================================================

        /// <summary>按当前系统配色与 DPI 准备全部 GDI 对象。DPI 或配色变化时自动重建。</summary>
        private void EnsureGdiResources()
        {
            if (_fontDpi != _dpi)
            {
                ReleaseGdiResources();
                _fontDpi = _dpi;
            }

            if (_hbrBack == IntPtr.Zero) _hbrBack = MakeSysBrush(PanelNative.COLOR_WINDOW);
            if (_hbrBorder == IntPtr.Zero) _hbrBorder = MakeSysBrush(PanelNative.COLOR_3DSHADOW);
            if (_hbrSel == IntPtr.Zero) _hbrSel = MakeSysBrush(PanelNative.COLOR_HIGHLIGHT);

            // 悬停色：高亮色与窗口底色按 HOVER_BLEND 混合，得到一层浅色调
            if (_hbrHover == IntPtr.Zero)
            {
                uint hi = SafeGetSysColor(PanelNative.COLOR_HIGHLIGHT);
                uint bg = SafeGetSysColor(PanelNative.COLOR_WINDOW);
                _hbrHover = MakeBrushRgb(
                    BlendChannel(hi, bg, 0),
                    BlendChannel(hi, bg, 8),
                    BlendChannel(hi, bg, 16));
            }
        }

        private void EnsureFonts()
        {
            if (_hFontMain != IntPtr.Zero && _hFontSmall != IntPtr.Zero) return;

            ReleaseFonts();

            string face = PickFontFace();
            _fontFaceName = face;

            _hFontMain = CreatePanelFont(face, -Scale(FONT_PX_MAIN), PanelNative.FW_NORMAL);
            _hFontSmall = CreatePanelFont(face, -Scale(FONT_PX_SMALL), PanelNative.FW_NORMAL);

            _mainLineHeight = MeasureLineHeight(_hFontMain, Scale(FONT_PX_MAIN));
            _smallLineHeight = MeasureLineHeight(_hFontSmall, Scale(FONT_PX_SMALL));
        }

        /// <summary>
        /// 按顺序挑第一个**真实存在**的字体族。
        /// 判据：建出来后 GetTextFaceW 读回的名字与请求的一致 ——
        /// GDI 找不到请求的族时会静默替换成别的字体，读回来的名字就不同了。
        /// 这比 EnumFontFamilies 省事，也不用额外 P/Invoke。
        /// </summary>
        private static string PickFontFace()
        {
            for (int i = 0; i < FontFaceCandidates.Length; i++)
            {
                string candidate = FontFaceCandidates[i];
                IntPtr hFont = IntPtr.Zero;
                try
                {
                    hFont = CreatePanelFont(candidate, -12, PanelNative.FW_NORMAL);
                    if (hFont == IntPtr.Zero) continue;

                    string realized = PanelNative.GetRealizedFaceName(hFont);
                    if (realized != null &&
                        string.Equals(realized.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // 探测失败 → 试下一个
                }
                finally
                {
                    if (hFont != IntPtr.Zero) SafeDeleteObject(hFont);
                }
            }

            // 都没命中：交给 GDI 的字体映射兜底（仍会挑一个能显示 CJK 的系统字体）
            return FontFaceCandidates[0];
        }

        private static IntPtr CreatePanelFont(string face, int height, int weight)
        {
            try
            {
                return PanelNative.CreateFontW(
                    height, 0, 0, 0, weight,
                    0, 0, 0,
                    PanelNative.DEFAULT_CHARSET,
                    PanelNative.OUT_TT_PRECIS,
                    PanelNative.CLIP_DEFAULT_PRECIS,
                    PanelNative.CLEARTYPE_QUALITY,
                    PanelNative.FF_SWISS,
                    face);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>取指定字体的 tmHeight；失败时退回请求高度。</summary>
        private static int MeasureLineHeight(IntPtr hFont, int fallback)
        {
            if (hFont == IntPtr.Zero) return fallback;

            IntPtr hdc = IntPtr.Zero;
            IntPtr hOld = IntPtr.Zero;
            try
            {
                hdc = PanelNative.GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return fallback;

                hOld = PanelNative.SelectObject(hdc, hFont);
                PanelNative.TEXTMETRICW tm;
                if (PanelNative.GetTextMetricsW(hdc, out tm) && tm.tmHeight > 0) return tm.tmHeight;
                return fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
            finally
            {
                if (hdc != IntPtr.Zero)
                {
                    try
                    {
                        if (hOld != IntPtr.Zero) PanelNative.SelectObject(hdc, hOld);
                        PanelNative.ReleaseDC(IntPtr.Zero, hdc);
                    }
                    catch (Exception) { }
                }
            }
        }

        private IntPtr MakeSysBrush(int sysColorIndex)
        {
            return MakeBrushRgbColorRef(SafeGetSysColor(sysColorIndex));
        }

        private static uint SafeGetSysColor(int index)
        {
            try
            {
                return PanelNative.GetSysColor(index);
            }
            catch (Exception)
            {
                // 极端兜底：白底黑字
                return index == PanelNative.COLOR_HIGHLIGHT ? PanelNative.Rgb(0x33, 0x99, 0xFF)
                     : index == PanelNative.COLOR_3DSHADOW ? PanelNative.Rgb(0xA0, 0xA0, 0xA0)
                     : PanelNative.Rgb(0xFF, 0xFF, 0xFF);
            }
        }

        private static IntPtr MakeBrushRgbColorRef(uint colorRef)
        {
            try
            {
                return PanelNative.CreateSolidBrush(colorRef);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        // COLORREF 是 0x00BBGGRR；shift 取某个通道并按比例与底色混合
        private static int BlendChannel(uint hi, uint bg, int shift)
        {
            int a = (int)((hi >> shift) & 0xFF);
            int b = (int)((bg >> shift) & 0xFF);
            int v = (int)(a * (1.0 - HOVER_BLEND) + b * HOVER_BLEND + 0.5);
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return v;
        }

        private static IntPtr MakeBrushRgb(int r, int g, int b)
        {
            return MakeBrushRgbColorRef(PanelNative.Rgb(r, g, b));
        }

        private void ReleaseFonts()
        {
            if (_hFontMain != IntPtr.Zero) { SafeDeleteObject(_hFontMain); _hFontMain = IntPtr.Zero; }
            if (_hFontSmall != IntPtr.Zero) { SafeDeleteObject(_hFontSmall); _hFontSmall = IntPtr.Zero; }
        }

        private void ReleaseGdiResources()
        {
            ReleaseFonts();
            if (_hbrBack != IntPtr.Zero) { SafeDeleteObject(_hbrBack); _hbrBack = IntPtr.Zero; }
            if (_hbrText != IntPtr.Zero) { SafeDeleteObject(_hbrText); _hbrText = IntPtr.Zero; }
            if (_hbrSel != IntPtr.Zero) { SafeDeleteObject(_hbrSel); _hbrSel = IntPtr.Zero; }
            if (_hbrHover != IntPtr.Zero) { SafeDeleteObject(_hbrHover); _hbrHover = IntPtr.Zero; }
            if (_hbrBorder != IntPtr.Zero) { SafeDeleteObject(_hbrBorder); _hbrBorder = IntPtr.Zero; }
        }

        private static void SafeDeleteObject(IntPtr ho)
        {
            if (ho == IntPtr.Zero) return;
            try { PanelNative.DeleteObject(ho); }
            catch (Exception) { }
        }

        // ====================================================================
        //  双缓冲
        // ====================================================================

        private IntPtr PrepareDoubleBuffer(IntPtr hdcScreen, int width, int height)
        {
            if (width <= 0 || height <= 0) return IntPtr.Zero;

            if (_hMemDc != IntPtr.Zero && _memWidth == width && _memHeight == height) return _hMemDc;

            ReleaseDoubleBuffer();

            IntPtr memDc = PanelNative.CreateCompatibleDC(hdcScreen);
            if (memDc == IntPtr.Zero) return IntPtr.Zero;

            IntPtr bmp = PanelNative.CreateCompatibleBitmap(hdcScreen, width, height);
            if (bmp == IntPtr.Zero)
            {
                try { PanelNative.DeleteDC(memDc); }
                catch (Exception) { }
                return IntPtr.Zero;
            }

            _hMemBmpOld = PanelNative.SelectObject(memDc, bmp);
            _hMemDc = memDc;
            _hMemBmp = bmp;
            _memWidth = width;
            _memHeight = height;
            return _hMemDc;
        }

        private void ReleaseDoubleBuffer()
        {
            try
            {
                if (_hMemDc != IntPtr.Zero)
                {
                    if (_hMemBmpOld != IntPtr.Zero) PanelNative.SelectObject(_hMemDc, _hMemBmpOld);
                    PanelNative.DeleteDC(_hMemDc);
                }
            }
            catch (Exception) { }
            finally
            {
                _hMemDc = IntPtr.Zero;
                _hMemBmpOld = IntPtr.Zero;
                _memWidth = 0;
                _memHeight = 0;
            }

            if (_hMemBmp != IntPtr.Zero)
            {
                SafeDeleteObject(_hMemBmp);
                _hMemBmp = IntPtr.Zero;
            }
        }

        // ====================================================================
        //  测量 + 布局
        // ====================================================================

        private static int ClampIndex(int index, int count)
        {
            if (count <= 0) return -1;
            if (index < 0) return 0;
            if (index >= count) return count - 1;
            return index;
        }

        /// <summary>
        /// 量出面板尺寸与各列位置。
        /// 宽 = 边框 + 内边距 + 序号列 + 间距 + 主文本列 + [间距 + 说明列] + 内边距 + 边框，
        /// 最后钳到 工作区宽×0.8、高×0.5。
        /// </summary>
        private void MeasureLayout(PanelItem[] items, PanelNative.RECT work, out int panelWidth, out int panelHeight)
        {
            _padX = Scale(BASE_PAD_X);
            _padY = Scale(BASE_PAD_Y);
            _colGap = Scale(BASE_COL_GAP);
            _border = 1;   // 边框恒为 1px，保持锐利（不随 DPI 放大，否则细边框会糊）

            int numW = 0;
            int dispW = 0;
            int noteW = 0;

            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = PanelNative.GetDC(IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    // ---- 序号列：量最大的那个序号（"10." 比 "9." 宽）----------
                    if (_hFontSmall != IntPtr.Zero)
                    {
                        IntPtr hOld = PanelNative.SelectObject(hdc, _hFontSmall);
                        for (int i = 0; i < items.Length; i++)
                        {
                            int w = MeasureTextWidth(hdc, (i + 1).ToString() + ".");
                            if (w > numW) numW = w;
                        }
                        if (hOld != IntPtr.Zero) PanelNative.SelectObject(hdc, hOld);
                    }

                    // ---- 主文本列（已形变的候选词）------------------------------
                    if (_hFontMain != IntPtr.Zero)
                    {
                        IntPtr hOld = PanelNative.SelectObject(hdc, _hFontMain);
                        for (int i = 0; i < items.Length; i++)
                        {
                            PanelItem it = items[i];
                            if (it == null) continue;
                            int w = MeasureTextWidth(hdc, it.Display);
                            if (w > dispW) dispW = w;
                        }
                        if (hOld != IntPtr.Zero) PanelNative.SelectObject(hdc, hOld);
                    }

                    // ---- 说明列：全部为空则整列不占宽度 -------------------------
                    if (_hFontSmall != IntPtr.Zero)
                    {
                        IntPtr hOld = PanelNative.SelectObject(hdc, _hFontSmall);
                        for (int i = 0; i < items.Length; i++)
                        {
                            PanelItem it = items[i];
                            if (it == null || !it.HasAnnotation) continue;
                            int w = MeasureTextWidth(hdc, it.Annotation);
                            if (w > noteW) noteW = w;
                        }
                        if (hOld != IntPtr.Zero) PanelNative.SelectObject(hdc, hOld);
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.HResult;
            }
            finally
            {
                if (hdc != IntPtr.Zero)
                {
                    try { PanelNative.ReleaseDC(IntPtr.Zero, hdc); }
                    catch (Exception) { }
                }
            }

            _numWidth = numW;
            _dispWidth = dispW;
            _noteWidth = noteW;

            // ---- 行高 ----------------------------------------------------------
            int lineH = _mainLineHeight > 0 ? _mainLineHeight : Scale(FONT_PX_MAIN);
            _rowHeight = lineH + _padY * 2;

            // ---- 状态行（可选）：拼音缓存 + "1/5" 之类 --------------------------
            int statusW = 0;
            if (_statusText != null && _hFontSmall != IntPtr.Zero)
            {
                IntPtr hdc2 = IntPtr.Zero;
                try
                {
                    hdc2 = PanelNative.GetDC(IntPtr.Zero);
                    if (hdc2 != IntPtr.Zero)
                    {
                        IntPtr hOld = PanelNative.SelectObject(hdc2, _hFontSmall);
                        statusW = MeasureTextWidth(hdc2, _statusText);
                        if (hOld != IntPtr.Zero) PanelNative.SelectObject(hdc2, hOld);
                    }
                }
                catch (Exception ex)
                {
                    _lastError = ex.HResult;
                }
                finally
                {
                    if (hdc2 != IntPtr.Zero)
                    {
                        try { PanelNative.ReleaseDC(IntPtr.Zero, hdc2); }
                        catch (Exception) { }
                    }
                }

                int smallH = _smallLineHeight > 0 ? _smallLineHeight : Scale(FONT_PX_SMALL);
                _statusHeight = smallH + Scale(BASE_STATUS_PAD_Y) * 2 + 1; // +1 = 分隔线
            }
            else
            {
                _statusHeight = 0;
            }
            _statusWidth = statusW;

            _rowsTop = _border + _statusHeight;

            // ---- 宽度 ----------------------------------------------------------
            int contentW = _padX + numW + _colGap + dispW;
            if (noteW > 0) contentW += _colGap + noteW;
            contentW += _padX;

            // 状态行比候选区宽时以状态行为准，否则文本会被裁掉
            int statusTotalW = statusW + _padX * 2;
            if (statusTotalW > contentW) contentW = statusTotalW;

            int minW = Scale(BASE_MIN_WIDTH);
            if (contentW < minW) contentW = minW;

            panelWidth = contentW + _border * 2;

            // ---- 高度 ----------------------------------------------------------
            panelHeight = _rowsTop + _rowHeight * items.Length + _border;

            // ---- 上限（工作区 × 比例）------------------------------------------
            int workW = work.right - work.left;
            int workH = work.bottom - work.top;

            if (workW > 0)
            {
                int maxW = (int)(workW * MAX_WIDTH_RATIO);
                if (maxW > 0 && panelWidth > maxW) panelWidth = maxW;
            }
            if (workH > 0)
            {
                int maxH = (int)(workH * MAX_HEIGHT_RATIO);
                if (maxH > 0 && panelHeight > maxH) panelHeight = maxH;
            }

            // 面板绝不能比工作区还大，否则钳制逻辑会算出自相矛盾的位置
            if (workW > 0 && panelWidth > workW) panelWidth = workW;
            if (workH > 0 && panelHeight > workH) panelHeight = workH;

            if (panelWidth < 1) panelWidth = 1;
            if (panelHeight < 1) panelHeight = 1;
        }

        /// <summary>测字符串像素宽（调用方保证 DC 里已选好字体）。</summary>
        private static int MeasureTextWidth(IntPtr hdc, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            try
            {
                PanelNative.SIZE sz;
                if (PanelNative.GetTextExtentPoint32W(hdc, text, text.Length, out sz))
                {
                    return sz.cx > 0 ? sz.cx : 0;
                }
            }
            catch (Exception) { }
            return 0;
        }

        // ====================================================================
        //  定位
        // ====================================================================

        /// <summary>
        /// 计算面板左上角屏幕坐标。
        /// 默认在锚点下方、左边缘对齐；下方放不下翻到上方；
        /// 最后无条件钳进工作区，保证整块可见。
        /// </summary>
        private void ComputePosition(PanelRect anchor, PanelNative.RECT work, int width, int height, out int x, out int y)
        {
            int gap = Scale(BASE_GAP_ANCHOR);

            int left = anchor.Left;
            int top;

            int belowSpace = work.bottom - anchor.Bottom;
            int aboveSpace = anchor.Top - work.top;

            if (belowSpace >= height + gap)
            {
                top = anchor.Bottom + gap;
            }
            else if (aboveSpace >= height + gap)
            {
                // 下方放不下 → 翻到锚点上方
                top = anchor.Top - gap - height;
            }
            else
            {
                // 上下都放不下：先按下方放，交给下面的钳制逻辑处理
                top = anchor.Bottom + gap;
            }

            int workW = work.right - work.left;
            int workH = work.bottom - work.top;

            // ---- 水平钳制 ------------------------------------------------------
            if (workW > 0 && width < workW)
            {
                if (left + width > work.right) left = work.right - width;
                if (left < work.left) left = work.left;
            }
            else
            {
                left = work.left;
            }

            // ---- 垂直钳制 ------------------------------------------------------
            if (workH > 0 && height < workH)
            {
                if (top + height > work.bottom) top = work.bottom - height;
                if (top < work.top) top = work.top;
            }
            else
            {
                top = work.top;
            }

            x = left;
            y = top;
        }

        /// <summary>
        /// 取锚点矩形所在（或最近）显示器的工作区。
        /// 用 MonitorFromRect + MONITOR_DEFAULTTONEAREST：插入符贴着屏幕边缘时，
        /// 矩形仍与最近显示器相交，比用单个点更稳。
        /// 任何一步失败都退回整块虚拟屏幕，绝不返回全零矩形（那会让面板跑到 0,0）。
        /// </summary>
        private static PanelNative.RECT GetWorkArea(PanelRect anchor)
        {
            try
            {
                PanelNative.RECT probe;
                probe.left = anchor.Left;
                probe.top = anchor.Top;
                probe.right = anchor.Right;
                probe.bottom = anchor.Bottom;

                IntPtr hMon = PanelNative.MonitorFromRect(ref probe, PanelNative.MONITOR_DEFAULTTONEAREST);
                if (hMon != IntPtr.Zero)
                {
                    PanelNative.MONITORINFO mi = new PanelNative.MONITORINFO();
                    mi.cbSize = Marshal.SizeOf(typeof(PanelNative.MONITORINFO));
                    if (PanelNative.GetMonitorInfoW(hMon, ref mi))
                    {
                        PanelNative.RECT work = mi.rcWork;
                        if (work.right > work.left && work.bottom > work.top) return work;
                    }
                }
            }
            catch (Exception) { }

            // 兜底：整块虚拟屏幕（多显示器下只会"更宽松"，不会跑飞）
            try
            {
                int vx = PanelNative.GetSystemMetrics(PanelNative.SM_XVIRTUALSCREEN);
                int vy = PanelNative.GetSystemMetrics(PanelNative.SM_YVIRTUALSCREEN);
                int vw = PanelNative.GetSystemMetrics(PanelNative.SM_CXVIRTUALSCREEN);
                int vh = PanelNative.GetSystemMetrics(PanelNative.SM_CYVIRTUALSCREEN);
                if (vw > 0 && vh > 0)
                {
                    PanelNative.RECT work;
                    work.left = vx;
                    work.top = vy;
                    work.right = vx + vw;
                    work.bottom = vy + vh;
                    return work;
                }
            }
            catch (Exception) { }

            // 最后兜底：一个不会导致自相矛盾钳制的极大区间
            PanelNative.RECT fallback;
            fallback.left = int.MinValue / 4;
            fallback.top = int.MinValue / 4;
            fallback.right = int.MaxValue / 4;
            fallback.bottom = int.MaxValue / 4;
            return fallback;
        }

        // ====================================================================
        //  窗口过程
        // ====================================================================

        private static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            CandidatePanel self = null;
            try
            {
                if (msg == PanelNative.WM_NCDESTROY)
                {
                    // 窗口正在消失：先解映射，再交给 DefWindowProc 走完销毁流程
                    self = LookupInstance(hWnd);
                    if (self != null)
                    {
                        self._hwnd = IntPtr.Zero;
                        self._visible = false;
                        self.FreeSelfHandle();
                    }
                    return PanelNative.DefWindowProcW(hWnd, msg, wParam, lParam);
                }

                self = LookupInstance(hWnd);
                if (self != null) return self.HandleMessage(hWnd, msg, wParam, lParam);
            }
            catch (Exception)
            {
                // WndProc 里的异常绝不能漏回系统 —— 会直接终结宿主进程
            }

            return PanelNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private static CandidatePanel LookupInstance(IntPtr hWnd)
        {
            try
            {
                IntPtr ptr = PanelNative.GetWindowLongPtrW(hWnd, PanelNative.GWLP_USERDATA);
                if (ptr == IntPtr.Zero) return null;
                GCHandle handle = GCHandle.FromIntPtr(ptr);
                if (!handle.IsAllocated) return null;
                return handle.Target as CandidatePanel;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private IntPtr HandleMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                // ---- 不抢焦点 ---------------------------------------------------
                case PanelNative.WM_MOUSEACTIVATE:
                    // 让这次点击"不激活本窗口"。
                    // ⚠ 必须用 MA_NOACTIVATE(3)，不能用 MA_NOACTIVATEANDEAT(4)：
                    //   4 会把这次点击整个吃掉，我们就收不到 WM_LBUTTONUP，点选功能会失效。
                    return new IntPtr(PanelNative.MA_NOACTIVATE);

                // ---- 交给 WM_PAINT 自己擦，避免闪烁 ------------------------------
                case PanelNative.WM_ERASEBKGND:
                    return new IntPtr(1);

                case PanelNative.WM_PAINT:
                    OnPaint(hWnd);
                    return IntPtr.Zero;

                case PanelNative.WM_PRINTCLIENT:
                    // wParam = 目标 DC。自绘窗口不处理它的话，PrintWindow /
                    // 录屏 / 辅助功能工具抓到的是空白。
                    OnPrintClient(hWnd, wParam);
                    return IntPtr.Zero;

                // ---- 系统配色变化：重建画刷后重绘 --------------------------------
                case PanelNative.WM_SYSCOLORCHANGE:
                    // 只删系统色画刷（字体沿用），下次绘制会自动按新配色重建
                    SafeDeleteObject(_hbrBack); _hbrBack = IntPtr.Zero;
                    SafeDeleteObject(_hbrSel); _hbrSel = IntPtr.Zero;
                    SafeDeleteObject(_hbrHover); _hbrHover = IntPtr.Zero;
                    SafeDeleteObject(_hbrBorder); _hbrBorder = IntPtr.Zero;
                    SafeInvalidate(hWnd);
                    break;

                // ---- 鼠标：悬停反馈 + 点选 ---------------------------------------
                case PanelNative.WM_MOUSEMOVE:
                    OnMouseMove(hWnd, lParam);
                    return IntPtr.Zero;

                case PanelNative.WM_MOUSELEAVE:
                    _trackingMouse = false;
                    if (_hoverIndex != -1)
                    {
                        _hoverIndex = -1;
                        SafeInvalidate(hWnd);
                    }
                    return IntPtr.Zero;

                case PanelNative.WM_LBUTTONDOWN:
                    // 吞掉按下消息，不让宿主同时处理这次点击
                    return IntPtr.Zero;

                case PanelNative.WM_LBUTTONUP:
                    OnLeftButtonUp(hWnd, lParam);
                    return IntPtr.Zero;

                // ---- 跨线程投递 ---------------------------------------------------
                case PanelNative.WM_XB_SHOW:
                    {
                        IntPtr p = wParam;
                        ShowRequest req = null;
                        try
                        {
                            GCHandle handle = GCHandle.FromIntPtr(p);
                            req = handle.Target as ShowRequest;
                            handle.Free();
                        }
                        catch (Exception) { }

                        if (req != null)
                        {
                            _statusText = req.StatusText;
                            ShowCore(req.Anchor, req.Items, req.SelectedIndex);
                        }
                        return IntPtr.Zero;
                    }

                case PanelNative.WM_XB_HIDE:
                    HideCore();
                    return IntPtr.Zero;

                case PanelNative.WM_XB_DESTROY:
                    ReleaseEverything();
                    return IntPtr.Zero;
            }

            return PanelNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private static void SafeInvalidate(IntPtr hWnd)
        {
            try { PanelNative.InvalidateRect(hWnd, IntPtr.Zero, false); }
            catch (Exception) { }
        }

        private void OnMouseMove(IntPtr hWnd, IntPtr lParam)
        {
            try
            {
                // 第一次进入客户区时登记 WM_MOUSELEAVE（这个登记是一次性的）
                if (!_trackingMouse)
                {
                    PanelNative.TRACKMOUSEEVENT tme = new PanelNative.TRACKMOUSEEVENT();
                    tme.cbSize = (uint)Marshal.SizeOf(typeof(PanelNative.TRACKMOUSEEVENT));
                    tme.dwFlags = PanelNative.TME_LEAVE;
                    tme.hwndTrack = hWnd;
                    tme.dwHoverTime = 0;
                    if (PanelNative.TrackMouseEvent(ref tme)) _trackingMouse = true;
                }

                int index = HitTestRow(lParam, hWnd);
                if (index != _hoverIndex)
                {
                    _hoverIndex = index;
                    SafeInvalidate(hWnd);
                }
            }
            catch (Exception) { }
        }

        private void OnLeftButtonUp(IntPtr hWnd, IntPtr lParam)
        {
            try
            {
                int index = HitTestRow(lParam, hWnd);
                if (index < 0 || index >= _items.Length) return;

                EventHandler<int> handler = ItemClicked;
                if (handler == null) return;

                handler(this, index);
            }
            catch (Exception)
            {
                // 订阅者的异常同样不能漏进宿主
            }
        }

        /// <summary>
        /// 把鼠标客户区坐标换算成候选行下标；不在任何行上时返回 -1。
        /// lParam 低 16 位 = X，高 16 位 = Y（有符号 16 位）。
        /// </summary>
        private int HitTestRow(IntPtr lParam, IntPtr hWnd)
        {
            if (_items.Length == 0 || _rowHeight <= 0) return -1;

            long packed = lParam.ToInt64();
            int x = (short)(packed & 0xFFFF);
            int y = (short)((packed >> 16) & 0xFFFF);

            // 客户区尺寸裁剪（防止把消息里的坐标当权威）
            PanelNative.RECT rc;
            if (PanelNative.GetClientRect(hWnd, out rc))
            {
                if (x < 0 || y < 0 || x >= rc.right || y >= rc.bottom) return -1;
            }

            if (y < _rowsTop) return -1;

            int index = (y - _rowsTop) / _rowHeight;
            if (index < 0 || index >= _items.Length) return -1;
            return index;
        }

        // ====================================================================
        //  绘制
        // ====================================================================

        private void OnPaint(IntPtr hWnd)
        {
            PanelNative.PAINTSTRUCT ps;
            IntPtr hdc;
            try
            {
                hdc = PanelNative.BeginPaint(hWnd, out ps);
            }
            catch (Exception)
            {
                return;
            }
            if (hdc == IntPtr.Zero) return;

            try
            {
                PanelNative.RECT rc;
                if (!PanelNative.GetClientRect(hWnd, out rc)) return;

                int w = rc.right - rc.left;
                int h = rc.bottom - rc.top;
                if (w <= 0 || h <= 0) return;

                // 双缓冲；失败就退回直接画到屏幕 DC（会闪但绝不崩）
                IntPtr memDc = PrepareDoubleBuffer(hdc, w, h);
                IntPtr target = memDc != IntPtr.Zero ? memDc : hdc;

                DrawPanel(target, w, h);

                if (memDc != IntPtr.Zero)
                {
                    PanelNative.BitBlt(hdc, 0, 0, w, h, memDc, 0, 0, PanelNative.SRCCOPY);
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.HResult;
            }
            finally
            {
                try { PanelNative.EndPaint(hWnd, ref ps); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// 响应 WM_PRINTCLIENT：把整个客户区画进调用方给的 DC。
        /// 这里**不**用 BeginPaint/EndPaint（那是 WM_PAINT 专用的），
        /// 也不做双缓冲 —— 目标 DC 本身就是离屏的。
        /// </summary>
        private void OnPrintClient(IntPtr hWnd, IntPtr hdcTarget)
        {
            if (hdcTarget == IntPtr.Zero) return;

            try
            {
                PanelNative.RECT rc;
                if (!PanelNative.GetClientRect(hWnd, out rc)) return;

                int w = rc.right - rc.left;
                int h = rc.bottom - rc.top;
                if (w <= 0 || h <= 0) return;

                DrawPanel(hdcTarget, w, h);
            }
            catch (Exception ex)
            {
                _lastError = ex.HResult;
            }
        }

        private void DrawPanel(IntPtr hdc, int width, int height)
        {
            // ---- 底色 ----------------------------------------------------------
            PanelNative.RECT full;
            full.left = 0; full.top = 0; full.right = width; full.bottom = height;
            FillRectSafe(hdc, ref full, _hbrBack);

            // ---- 状态行（可选）+ 分隔线 -----------------------------------------
            if (_statusHeight > 0 && _statusText != null && _hFontSmall != IntPtr.Zero)
            {
                int smallH = _smallLineHeight > 0 ? _smallLineHeight : Scale(FONT_PX_SMALL);
                int textTop = _border + Scale(BASE_STATUS_PAD_Y)
                            + (smallH - _smallLineHeight) / 2;

                PanelNative.RECT statusRc;
                statusRc.left = _border + _padX;
                statusRc.top = textTop;
                statusRc.right = width - _border - _padX;
                statusRc.bottom = textTop + smallH;

                IntPtr hOld = SelectIfValid(hdc, _hFontSmall);
                uint oldColor = SetTextColorSafe(hdc, SafeGetSysColor(PanelNative.COLOR_GRAYTEXT));
                int oldBk = SetBkModeSafe(hdc, PanelNative.TRANSPARENT);
                DrawTextSafe(hdc, _statusText, ref statusRc,
                    PanelNative.DT_SINGLELINE | PanelNative.DT_VCENTER |
                    PanelNative.DT_NOPREFIX | PanelNative.DT_END_ELLIPSIS);
                SetTextColorSafe(hdc, oldColor);
                if (oldBk != 0) SetBkModeSafe(hdc, oldBk);
                RestoreFont(hdc, hOld);

                // 分隔线：状态行与候选区之间的一道细线
                PanelNative.RECT sep;
                sep.left = _border;
                sep.top = _border + _statusHeight - 1;
                sep.right = width - _border;
                sep.bottom = sep.top + 1;
                FillRectSafe(hdc, ref sep, _hbrBorder);
            }

            // ---- 候选行 --------------------------------------------------------
            for (int i = 0; i < _items.Length; i++)
            {
                int rowTop = _rowsTop + i * _rowHeight;
                if (rowTop >= height) break;

                int rowBottom = rowTop + _rowHeight;
                if (rowBottom > height) rowBottom = height;

                DrawRow(hdc, i, rowTop, rowBottom, width);
            }

            // ---- 1px 边框（最后画，压在最上层）----------------------------------
            if (_hbrBorder != IntPtr.Zero)
            {
                try { PanelNative.FrameRect(hdc, ref full, _hbrBorder); }
                catch (Exception) { }
            }
        }

        private void DrawRow(IntPtr hdc, int index, int rowTop, int rowBottom, int width)
        {
            PanelItem item = _items[index];
            bool selected = (index == _selectedIndex);
            bool hovered = (index == _hoverIndex);

            PanelNative.RECT row;
            row.left = _border;
            row.top = rowTop;
            row.right = width - _border;
            row.bottom = rowBottom;

            // ---- 行底色：选中 > 悬停 > 普通（普通即面板底色，无需再填）----------
            if (selected)
            {
                FillRectSafe(hdc, ref row, _hbrSel);
            }
            else if (hovered)
            {
                FillRectSafe(hdc, ref row, _hbrHover);
            }

            // ---- 文字颜色（跟随系统高对比度主题）-------------------------------
            uint mainColor = selected
                ? SafeGetSysColor(PanelNative.COLOR_HIGHLIGHTTEXT)
                : SafeGetSysColor(PanelNative.COLOR_WINDOWTEXT);
            uint subColor = selected
                ? SafeGetSysColor(PanelNative.COLOR_HIGHLIGHTTEXT)
                : SafeGetSysColor(PanelNative.COLOR_GRAYTEXT);

            int oldBk = SetBkModeSafe(hdc, PanelNative.TRANSPARENT);

            // ---- 序号列（右对齐，"9." 与 "10." 才能对齐）------------------------
            if (_numWidth > 0 && _hFontSmall != IntPtr.Zero)
            {
                PanelNative.RECT numRc;
                numRc.left = _border + _padX;
                numRc.top = rowTop;
                numRc.right = numRc.left + _numWidth;
                numRc.bottom = rowBottom;

                IntPtr hOld = SelectIfValid(hdc, _hFontSmall);
                uint oldColor = SetTextColorSafe(hdc, subColor);
                DrawTextSafe(hdc, (index + 1).ToString() + ".", ref numRc,
                    PanelNative.DT_SINGLELINE | PanelNative.DT_VCENTER |
                    PanelNative.DT_NOPREFIX | PanelNative.DT_RIGHT);
                SetTextColorSafe(hdc, oldColor);
                RestoreFont(hdc, hOld);
            }

            int dispLeft = _border + _padX + _numWidth + _colGap;
            int noteLeft = width - _border - _padX - _noteWidth;

            // ---- 主文本（已形变的候选词）----------------------------------------
            if (item != null && _hFontMain != IntPtr.Zero)
            {
                string display = item.Display;
                if (!string.IsNullOrEmpty(display))
                {
                    int right = (_noteWidth > 0) ? noteLeft - _colGap : width - _border - _padX;
                    if (right > dispLeft)
                    {
                        PanelNative.RECT dispRc;
                        dispRc.left = dispLeft;
                        dispRc.top = rowTop;
                        dispRc.right = right;
                        dispRc.bottom = rowBottom;

                        IntPtr hOld = SelectIfValid(hdc, _hFontMain);
                        uint oldColor = SetTextColorSafe(hdc, mainColor);
                        // DT_END_ELLIPSIS：文本超宽时截断加省略号，绝不画出面板外
                        DrawTextSafe(hdc, display, ref dispRc,
                            PanelNative.DT_SINGLELINE | PanelNative.DT_VCENTER |
                            PanelNative.DT_NOPREFIX | PanelNative.DT_END_ELLIPSIS);
                        SetTextColorSafe(hdc, oldColor);
                        RestoreFont(hdc, hOld);
                    }
                }
            }

            // ---- 说明列（更小更灰，右对齐）--------------------------------------
            if (item != null && item.HasAnnotation && _noteWidth > 0 && _hFontSmall != IntPtr.Zero)
            {
                PanelNative.RECT noteRc;
                noteRc.left = noteLeft;
                noteRc.top = rowTop;
                noteRc.right = width - _border - _padX;
                noteRc.bottom = rowBottom;

                // 主文本按单行垂直居中，说明用小一号字，单独按自己的行高居中
                int noteH = _smallLineHeight;
                int center = (rowBottom - rowTop - noteH) / 2;
                if (center > 0)
                {
                    noteRc.top += center;
                    noteRc.bottom = noteRc.top + noteH;
                }

                IntPtr hOld = SelectIfValid(hdc, _hFontSmall);
                uint oldColor = SetTextColorSafe(hdc, subColor);
                DrawTextSafe(hdc, item.Annotation, ref noteRc,
                    PanelNative.DT_SINGLELINE | PanelNative.DT_VCENTER |
                    PanelNative.DT_NOPREFIX | PanelNative.DT_END_ELLIPSIS | PanelNative.DT_RIGHT);
                SetTextColorSafe(hdc, oldColor);
                RestoreFont(hdc, hOld);
            }

            if (oldBk != 0) SetBkModeSafe(hdc, oldBk);
        }

        // ====================================================================
        //  绘制小工具（每一步都吞异常，保证画到一半失败也不会炸）
        // ====================================================================

        private static void FillRectSafe(IntPtr hdc, ref PanelNative.RECT rc, IntPtr hbr)
        {
            if (hbr == IntPtr.Zero) return;
            try { PanelNative.FillRect(hdc, ref rc, hbr); }
            catch (Exception) { }
        }

        private static IntPtr SelectIfValid(IntPtr hdc, IntPtr hgdiObj)
        {
            if (hgdiObj == IntPtr.Zero) return IntPtr.Zero;
            try { return PanelNative.SelectObject(hdc, hgdiObj); }
            catch (Exception) { return IntPtr.Zero; }
        }

        /// <summary>还原 DC 里原来的字体。SelectObject 失败时返回的 (HGDIOBJ)-1 不能再用。</summary>
        private static void RestoreFont(IntPtr hdc, IntPtr hOldFont)
        {
            if (hOldFont == IntPtr.Zero) return;
            try
            {
                if (hOldFont != new IntPtr(-1)) PanelNative.SelectObject(hdc, hOldFont);
            }
            catch (Exception) { }
        }

        private static uint SetTextColorSafe(IntPtr hdc, uint color)
        {
            try { return PanelNative.SetTextColor(hdc, color); }
            catch (Exception) { return 0; }
        }

        private static int SetBkModeSafe(IntPtr hdc, int mode)
        {
            try { return PanelNative.SetBkMode(hdc, mode); }
            catch (Exception) { return 0; }
        }

        private static void DrawTextSafe(IntPtr hdc, string text, ref PanelNative.RECT rc, uint format)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { PanelNative.DrawTextW(hdc, text, text.Length, ref rc, format); }
            catch (Exception) { }
        }

        // ====================================================================
        //  内部类型
        // ====================================================================

        /// <summary>跨线程 Show 的载荷。装箱进 GCHandle 用 PostMessage 投递到 UI 线程。</summary>
        private sealed class ShowRequest
        {
            public PanelRect Anchor;
            public PanelItem[] Items;
            public int SelectedIndex;
            public string StatusText;
        }

        /// <summary>自定义错误码：窗口尚未创建时从非 UI 线程调用 Show。</summary>
        private const int ERROR_NOT_ON_UI_THREAD = unchecked((int)0x80070005); // E_ACCESSDENIED
    }
}
