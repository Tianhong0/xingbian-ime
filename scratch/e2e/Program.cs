// 端到端验证 harness 主程序。
//
// 目标：在**不注册 TIP** 的前提下，证明「按键 → 候选 → 上屏」这条链路真的能工作。
//
// 做法：本进程内用真实的 msctf.dll（TF_GetThreadMgr）拿到 ITfThreadMgr，
//       自己实现一个假 ITextStoreACP 当宿主，让 TSF 愿意为我们的 ITfContext
//       开编辑会话，然后直接 new 出被测的 Xingbian.Tsf.TextService 并模拟按键。
//
// 判定：敲完 n i m a + 空格 之后，假文本存储里的文本必须精确等于 "亻尔 女马"。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Xingbian.Core;
using Xingbian.Tsf;
using Xingbian.Tsf.Interop;

namespace Xingbian.E2E
{
    internal sealed class Rig
    {
        public FakeTextStore Store;
        public ITfContext Context;
        public ITfDocumentMgr DocMgr;
        public uint TextStoreEc;
        public string SetupError;
    }

    internal sealed class KeyEvent
    {
        public int Vk;
        public bool TestEaten;
        public bool DownCalled;
        public bool DownEaten;
        public int HrTest;
        public int HrDown;
    }

    internal sealed class CaseResult
    {
        public string Id;
        public string Title;
        public readonly List<string> Observations = new List<string>();
        public bool Passed;
        public bool Skipped;

        /// <summary>对照实验：预期就是「失败」的（例如模型 B 写不进存储）。Passed=true 表示
        /// 它表现得和预期一致；渲染成 EXPECTED-FAIL，绝不冒充成 PASS。</summary>
        public bool ExpectedFail = false;

        public string Verdict = "";

        public void Obs(string s) { Observations.Add(s); }
    }

    internal static class Program
    {
        // -------------------------------------------------------------------------------
        // 常量
        // -------------------------------------------------------------------------------

        private const string ProjectRoot = @"D:\Documents\deepseek-harness\default-workspace\xingbian-ime";
        private const string DictPath = ProjectRoot + @"\assets\build\xingbian.bin";
        private const string OutDir = ProjectRoot + @"\scratch\e2e";

        private const int VK_BACK = 0x08;
        private const int VK_RETURN = 0x0D;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_N = 0x4E;
        private const int VK_I = 0x49;
        private const int VK_M = 0x4D;
        private const int VK_A = 0x41;
        private const int VK_1 = 0x31;
        private const int VK_2 = 0x32;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;

        /// <summary>一个“合理”的 keydown lParam（repeat=1, scancode=0x1C, 非 extended）。</summary>
        private static readonly IntPtr KeyLParam = new IntPtr(0x001C0001);

        private const string ExpectedMain = "亻尔 女马";
        private const string ExpectedNi = "亻尔";
        private const string PanelClassName = "XingbianIME.CandidatePanel.Window";

        // -------------------------------------------------------------------------------
        // 全局状态
        // -------------------------------------------------------------------------------

        private static readonly List<string> Console2 = new List<string>();
        private static readonly List<CaseResult> Results = new List<CaseResult>();

        private static TextService _svc;
        private static ITfThreadMgr _ptim;
        private static uint _tid;
        private static IntPtr _hwndHost;
        private static Win32.WndProc _hostProc;   // 必须保持引用，否则委托被回收

        private static readonly List<string> GlobalLog = new List<string>();

        /// <summary>本次运行创建过的所有被测实例（收尾时统一 Deactivate，不给线程管理器留悬挂 sink）。</summary>
        private static readonly List<TextService> AllSvcs = new List<TextService>();

        // -------------------------------------------------------------------------------
        // 主入口
        // -------------------------------------------------------------------------------

        [STAThread]
        private static int Main(string[] args)
        {
            int exit = 0;
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception)
            {
                // 无控制台或重定向失败都不影响结果文件。
            }

            LockModel mainModel = LockModel.SyncGrant;
            bool modelBOnly = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--modelb", StringComparison.OrdinalIgnoreCase)) { modelBOnly = true; }
                if (string.Equals(args[i], "--deferred", StringComparison.OrdinalIgnoreCase)) { mainModel = LockModel.DeferredGrant; }
            }

            try
            {
                exit = Run(mainModel, modelBOnly);
            }
            catch (Exception ex)
            {
                Say("");
                Say("!!! harness 顶层未捕获异常（这本身就是结果）：");
                Say(ex.ToString());
                exit = 3;
            }

            try
            {
                WriteArtifacts();
            }
            catch (Exception ex)
            {
                Console.WriteLine("写结果文件失败: " + ex.Message);
            }

            return exit;
        }

        private static int Run(LockModel mainModel, bool modelBOnly)
        {
            Say("================ Xingbian IME 端到端 harness ================");
            Say("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Say("进程: PID=" + System.Diagnostics.Process.GetCurrentProcess().Id
                + " 线程=" + Thread.CurrentThread.ManagedThreadId
                + " ApartmentState=" + Thread.CurrentThread.GetApartmentState());
            Say("os: " + Environment.OSVersion.VersionString + " / CLR " + Environment.Version);

            // 上次运行如果往系统里注入过 Ctrl/Alt 且没松开，状态会残留到本次运行，
            // 让 ShouldEat() 开头 HasControlOrAlt() 恒为真 —— 全部用例都会莫名其妙失败。
            Say("[env] 启动时先松开所有修饰键: " + ReleaseAllModifiers());
            Say("");

            // ---- 0. 环境准备 ----------------------------------------------------------
            Environment.SetEnvironmentVariable("XINGBIAN_DICT", DictPath);
            Say("[env] XINGBIAN_DICT=" + Environment.GetEnvironmentVariable("XINGBIAN_DICT"));
            Say("[env] 词典文件存在=" + File.Exists(DictPath)
                + (File.Exists(DictPath) ? (" 大小=" + new FileInfo(DictPath).Length) : ""));

            string warmupErr;
            bool warm = TextService.Warmup(out warmupErr);
            Say("[engine] TextService.Warmup -> " + warm + "  error=" + (warmupErr == null ? "(null)" : warmupErr));
            Say("[engine] TextService.EngineError=" + (TextService.EngineError == null ? "(null)" : TextService.EngineError));
            try
            {
                XingbianEngine probe = XingbianEngine.CreateDefault();
                Say("[engine] XingbianEngine.CreateDefault() -> " + (probe == null ? "null" : ("DictPath=" + probe.DictPath)));
            }
            catch (Exception ex)
            {
                Say("[engine] XingbianEngine.CreateDefault() 抛异常: " + ex.GetType().Name + ": " + ex.Message);
            }

            Type t = typeof(TextService);
            StringBuilder ifaces = new StringBuilder();
            Type[] impl = t.GetInterfaces();
            for (int i = 0; i < impl.Length; i++)
            {
                if (i > 0) { ifaces.Append(", "); }
                ifaces.Append(impl[i].Name);
            }
            Say("[sut] typeof(TextService).GetInterfaces() = " + ifaces);
            Say("");

            // ---- 1. 建宿主窗口 --------------------------------------------------------
            _hostProc = HostWndProc;
            _hwndHost = CreateHostWindow();
            Say("[host] 隐藏宿主窗口 hwnd=0x" + _hwndHost.ToInt64().ToString("X")
                + (Win32.GetModuleHandleW(null) != IntPtr.Zero ? "" : ""));
            if (_hwndHost == IntPtr.Zero)
            {
                Say("[host] !! CreateWindowExW 失败 err=" + Marshal.GetLastWin32Error());
            }

            // ---- 2. 拿真实 TSF 线程管理器 --------------------------------------------
            int hr = E2ENative.TF_GetThreadMgr(out _ptim);
            Say("[tsf] TF_GetThreadMgr -> hr=0x" + hr.ToString("X8") + " ptim=" + (_ptim == null ? "null" : "OK"));
            if (_ptim == null)
            {
                int hr2 = E2ENative.TF_CreateThreadMgr(out _ptim);
                Say("[tsf] TF_CreateThreadMgr -> hr=0x" + hr2.ToString("X8") + " ptim=" + (_ptim == null ? "null" : "OK"));
            }
            if (_ptim == null)
            {
                Say("[tsf] 拿不到线程管理器，后面全部无法进行。");
                return 2;
            }
            Say("[tsf] ITfThreadMgr 实例=" + _ptim.GetType().FullName);

            int hrAct = _ptim.Activate(out _tid);
            Say("[tsf] ITfThreadMgr::Activate -> hr=0x" + hrAct.ToString("X8") + " tid=" + _tid);
            if (hrAct != 0)
            {
                Say("[tsf] Activate 失败，后面无法进行。");
                return 2;
            }
            Say("");

            // ---- 3. new 出被测 TextService 并激活 ------------------------------------
            // 这条实例只用于证明 ActivateEx 这个入口本身可用；后续每个用例都会由 NewRig()
            // 再各起一个全新实例，避免实例状态（中/英模式、缓冲区、_shiftChord）在用例间串味。
            _svc = new TextService();
            int hrSv = _svc.ActivateEx(_ptim, _tid, 0);
            AllSvcs.Add(_svc);
            Say("[sut] new TextService() + ActivateEx(tid=" + _tid + ") -> hr=0x" + hrSv.ToString("X8"));
            Say("[sut] 说明：每个用例都会通过 NewRig() 另起一个全新实例，实例之间不共享模式/缓冲区状态。");
            Say("");

            // ---- 用例 6：空缓冲区不吞功能键（必须在任何字母之前跑） -----------------
            Results.Add(Case6_EmptyBuffer(mainModel));

            if (!modelBOnly)
            {
                Results.Add(Case1_MainChain(mainModel));
                Results.Add(Case2_LettersEaten(mainModel));
                Results.Add(Case3_CandidateOrder(mainModel));
                Results.Add(Case4_Escape(mainModel));
                Results.Add(Case5_Backspace(mainModel));
                Results.Add(Case7_CtrlAlt());
                Results.Add(Case8_ShiftToggle());
                Results.Add(Case9_Panel());
                Results.Add(Case10_NullContext());
            }

            // ---- 附加实验：RequestLock 的两种模型对照 -------------------------------
            Results.Add(Experiment_LockModelA());
            Results.Add(Experiment_LockModelB());

            // ---- 附加：Activate() 入口 -------------------------------------------------
            Results.Add(Extra_ActivateEntry());

            // ---- 收尾 ------------------------------------------------------------------
            Say("");
            Say("================ 汇总 ================");
            int pass = 0, fail = 0, skip = 0, xfail = 0;
            for (int i = 0; i < Results.Count; i++)
            {
                CaseResult r = Results[i];
                string tag;
                if (r.Skipped) { tag = "SKIP"; skip++; }
                else if (r.ExpectedFail)
                {
                    // 对照实验：预期就是失败。Passed=true 表示「和预期一致」。
                    tag = r.Passed ? "EXPECTED-FAIL" : "UNEXPECTED-PASS";
                    xfail++;
                }
                else if (r.Passed) { tag = "PASS"; pass++; }
                else { tag = "FAIL"; fail++; }
                Say("  [" + tag + "] " + r.Id + " " + r.Title + "  " + r.Verdict);
            }
            Say("");
            Say("PASS=" + pass + " FAIL=" + fail + " SKIP=" + skip
                + " （另有对照实验 " + xfail + " 条，按设计预期失败，不计入 FAIL）");

            try
            {
                for (int i = 0; i < AllSvcs.Count; i++)
                {
                    try { AllSvcs[i].Deactivate(); }
                    catch (Exception ex) { Say("[sut] 实例 " + i + " Deactivate() 抛异常: " + ex.GetType().Name + ": " + ex.Message); }
                }
                Say("[sut] " + AllSvcs.Count + " 个 TextService 实例全部 Deactivate() 正常返回");
            }
            catch (Exception ex)
            {
                Say("[sut] Deactivate() 抛异常: " + ex.GetType().Name + ": " + ex.Message);
            }

            return fail == 0 ? 0 : 1;
        }

        // ===============================================================================
        // 用例 1：主链路
        // ===============================================================================

        private static CaseResult Case1_MainChain(LockModel model)
        {
            CaseResult c = new CaseResult();
            c.Id = "用例1";
            c.Title = "主链路 n i m a + 空格 → 假存储文本 == \"亻尔 女马\"";

            List<string> log = new List<string>();
            Rig rig = NewRig(model, log);
            if (rig.SetupError != null)
            {
                c.Obs("建 context 失败: " + rig.SetupError);
                c.Verdict = "无法建立 ITfContext";
                c.Passed = false;
                DumpLog(c, log);
                return c;
            }
            c.Obs("RequestLock 模型 = " + model);
            c.Obs("CreateContext 返回的 ecTextStore=" + rig.TextStoreEc);

            PrepareSut(rig.Context, log);

            List<KeyEvent> keys = TypeKeys(rig.Context, new int[] { VK_N, VK_I, VK_M, VK_A }, log);
            for (int i = 0; i < keys.Count; i++)
            {
                c.Obs("按键 " + VkName(keys[i].Vk) + " : OnTestKeyDown eaten=" + keys[i].TestEaten
                    + " hr=0x" + keys[i].HrTest.ToString("X8")
                    + " ; OnKeyDown 被调用=" + keys[i].DownCalled
                    + (keys[i].DownCalled ? (" eaten=" + keys[i].DownEaten + " hr=0x" + keys[i].HrDown.ToString("X8")) : ""));
            }
            c.Obs("敲完 nima 后 buffer 状态（间接）：敲空格是否被吞将说明 buffer 非空");

            KeyEvent space = SendKey(rig.Context, VK_SPACE, log);
            c.Obs("按键 SPACE : OnTestKeyDown eaten=" + space.TestEaten
                + " ; OnKeyDown 被调用=" + space.DownCalled
                + (space.DownCalled ? (" eaten=" + space.DownEaten + " hr=0x" + space.HrDown.ToString("X8")) : ""));

            Win32.Pump();

            c.Obs("★ 假文本存储最终文本 = " + Describe(rig.Store.Text));
            c.Obs("InsertTextAtSelection 被调用次数 = " + rig.Store.InsertTextAtSelectionCalls);
            c.Obs("RequestLock 被调用次数 = " + rig.Store.RequestLockCalls
                + " , OnLockGranted 被调用次数 = " + rig.Store.OnLockGrantedCalls);
            c.Obs("最后一次 RequestLock 的 flags = 0x" + rig.Store.LastLockFlags.ToString("X"));
            c.Obs("RequestEditSession 之后 store 文本 = " + Describe(rig.Store.Text));

            bool exact = string.Equals(rig.Store.Text, ExpectedMain, StringComparison.Ordinal);
            c.Passed = exact;
            c.Verdict = exact
                ? "文本精确匹配"
                : ("期望 " + Describe(ExpectedMain) + " 实际 " + Describe(rig.Store.Text));
            DumpLog(c, log);
            return c;
        }

        // ===============================================================================
        // 用例 2：四个字母每个都被吞掉
        // ===============================================================================

        private static CaseResult Case2_LettersEaten(LockModel model)
        {
            CaseResult c = new CaseResult();
            c.Id = "用例2";
            c.Title = "四个字母 n i m a 每个 OnTestKeyDown 都返回 eaten=true";

            List<string> log = new List<string>();
            Rig rig = NewRig(model, log);
            if (rig.SetupError != null)
            {
                c.Obs("建 context 失败: " + rig.SetupError);
                c.Verdict = "无法建立 ITfContext";
                c.Passed = false;
                DumpLog(c, log);
                return c;
            }
            PrepareSut(rig.Context, log);

            int[] vks = new int[] { VK_N, VK_I, VK_M, VK_A };
            List<KeyEvent> keys = TypeKeys(rig.Context, vks, log);
            bool all = keys.Count == 4;
            for (int i = 0; i < keys.Count; i++)
            {
                c.Obs("VK 0x" + keys[i].Vk.ToString("X2") + " (" + VkName(keys[i].Vk) + ") OnTestKeyDown eaten="
                    + keys[i].TestEaten + " hr=0x" + keys[i].HrTest.ToString("X8")
                    + " ; OnKeyDown 也被调用=" + keys[i].DownCalled
                    + (keys[i].DownCalled ? (" 其 eaten=" + keys[i].DownEaten) : ""));
                if (!keys[i].TestEaten) { all = false; }
            }
            c.Passed = all;
            c.Verdict = all ? "四个字母全部被吞" : "有字母未被吞";
            DumpLog(c, log);
            return c;
        }

        // ===============================================================================
        // 用例 3：候选排序（用数字键 1 / 2 间接验证）
        // ===============================================================================

        private static CaseResult Case3_CandidateOrder(LockModel model)
        {
            CaseResult c = new CaseResult();
            c.Id = "用例3";
            c.Title = "候选排序：nima+1 应等于\"亻尔 女马\"，nima+2 应不同";

            string textDigit1 = TypeThenCommitWithDigit(model, VK_1, c, "数字键 1");
            string textDigit2 = TypeThenCommitWithDigit(model, VK_2, c, "数字键 2");

            c.Obs("nima + 数字键1 → " + Describe(textDigit1));
            c.Obs("nima + 数字键2 → " + Describe(textDigit2));
            c.Obs("两次结果相同? " + string.Equals(textDigit1, textDigit2, StringComparison.Ordinal));

            bool first_ok = string.Equals(textDigit1, ExpectedMain, StringComparison.Ordinal);
            bool second_diff = !string.Equals(textDigit2, ExpectedMain, StringComparison.Ordinal);
            c.Passed = first_ok && second_diff;
            c.Verdict = "1号候选=" + (first_ok ? "匹配" : "不匹配")
                + " ; 2号候选与1号候选" + (string.Equals(textDigit1, textDigit2, StringComparison.Ordinal) ? "相同（异常）" : "不同");
            return c;
        }

        private static string TypeThenCommitWithDigit(LockModel model, int digitVk, CaseResult c, string label)
        {
            List<string> log = new List<string>();
            Rig rig = NewRig(model, log);
            if (rig.SetupError != null)
            {
                c.Obs(label + " 建 context 失败: " + rig.SetupError);
                return "<建 context 失败>";
            }
            PrepareSut(rig.Context, log);
            TypeKeys(rig.Context, new int[] { VK_N, VK_I, VK_M, VK_A }, log);
            KeyEvent d = SendKey(rig.Context, digitVk, log);
            c.Obs(label + " : OnTestKeyDown eaten=" + d.TestEaten
                + " ; OnKeyDown 被调用=" + d.DownCalled
                + (d.DownCalled ? (" eaten=" + d.DownEaten + " hr=0x" + d.HrDown.ToString("X8")) : ""));
            Win32.Pump();
            c.Obs(label + " : InsertTextAtSelection 次数=" + rig.Store.InsertTextAtSelectionCalls
                + " , store 文本=" + Describe(rig.Store.Text));
            return rig.Store.Text;
        }

        // ===============================================================================
        // 用例 4：Esc 取消
        // ===============================================================================

        private static CaseResult Case4_Escape(LockModel model)
        {
            CaseResult c = new CaseResult();
            c.Id = "用例4";
            c.Title = "Esc 取消：nima+Esc 后存储仍为空，且随后空格不被吞";

            List<string> log = new List<string>();
            Rig rig = NewRig(model, log);
            if (rig.SetupError != null)
            {
                c.Obs("建 context 失败: " + rig.SetupError);
                c.Verdict = "无法建立 ITfContext";
                c.Passed = false;
                return c;
            }
            PrepareSut(rig.Context, log);
            TypeKeys(rig.Context, new int[] { VK_N, VK_I, VK_M, VK_A }, log);

            KeyEvent esc = SendKey(rig.Context, VK_ESCAPE, log);
            c.Obs("ESC : OnTestKeyDown eaten=" + esc.TestEaten
                + " ; OnKeyDown 被调用=" + esc.DownCalled
                + (esc.DownCalled ? (" eaten=" + esc.DownEaten + " hr=0x" + esc.HrDown.ToString("X8")) : ""));
            Win32.Pump();
            c.Obs("ESC 之后 store 文本 = " + Describe(rig.Store.Text));
            c.Obs("ESC 之后 InsertTextAtSelection 次数 = " + rig.Store.InsertTextAtSelectionCalls);

            KeyEvent space = SendKey(rig.Context, VK_SPACE, log);
            c.Obs("ESC 之后 SPACE : OnTestKeyDown eaten=" + space.TestEaten
                + " ; OnKeyDown 被调用=" + space.DownCalled);
            Win32.Pump();

            bool empty = rig.Store.TextLength == 0;
            bool spaceNotEaten = !space.TestEaten;
            c.Passed = empty && spaceNotEaten;
            c.Verdict = "存储为空=" + empty + " ; 空格不被吞=" + spaceNotEaten;
            DumpLog(c, log);
            return c;
        }

        // ===============================================================================
        // 用例 5：Backspace
        // ===============================================================================

        private static CaseResult Case5_Backspace(LockModel model)
        {
            CaseResult c = new CaseResult();
            c.Id = "用例5";
            c.Title = "Backspace：nima+BS×2 → 空格上屏应为\"亻尔\"；n+BS 后空格不被吞";

            // --- 5a: nima + 两次退格 ---
            List<string> log = new List<string>();
            Rig rig = NewRig(model, log);
            if (rig.SetupError != null)
            {
                c.Obs("建 context 失败: " + rig.SetupError);
                c.Verdict = "无法建立 ITfContext";
                c.Passed = false;
                return c;
            }
            PrepareSut(rig.Context, log);
            TypeKeys(rig.Context, new int[] { VK_N, VK_I, VK_M, VK_A }, log);

            KeyEvent bs1 = SendKey(rig.Context, VK_BACK, log);
            KeyEvent bs2 = SendKey(rig.Context, VK_BACK, log);
            c.Obs("第 1 次 BS : OnTestKeyDown eaten=" + bs1.TestEaten + " , OnKeyDown 被调用=" + bs1.DownCalled
                + (bs1.DownCalled ? (" eaten=" + bs1.DownEaten) : ""));
            c.Obs("第 2 次 BS : OnTestKeyDown eaten=" + bs2.TestEaten + " , OnKeyDown 被调用=" + bs2.DownCalled
                + (bs2.DownCalled ? (" eaten=" + bs2.DownEaten) : ""));

            KeyEvent sp = SendKey(rig.Context, VK_SPACE, log);
            c.Obs("两次 BS 之后 SPACE : OnTestKeyDown eaten=" + sp.TestEaten + " , OnKeyDown 被调用=" + sp.DownCalled);
            Win32.Pump();
            string actual = rig.Store.Text;
            c.Obs("★ nima+BS×2+空格 → 实际上屏文本 = " + Describe(actual)
                + " （参考：nim 的首候选亦为「你妈」；再退一次得到 ni）");
            bool a_ok = string.Equals(actual, ExpectedNi, StringComparison.Ordinal);

            // --- 5b: n + 一次退格 → buffer 清空 → 空格不被吞 ---
            List<string> log2 = new List<string>();
            Rig rig2 = NewRig(model, log2);
            if (rig2.SetupError != null)
            {
                c.Obs("5b 建 context 失败: " + rig2.SetupError);
                c.Verdict = "无法建立 ITfContext";
                c.Passed = false;
                return c;
            }
            PrepareSut(rig2.Context, log2);
            TypeKeys(rig2.Context, new int[] { VK_N }, log2);
            KeyEvent bs = SendKey(rig2.Context, VK_BACK, log2);
            c.Obs("5b: 敲 n 后 BS : OnTestKeyDown eaten=" + bs.TestEaten + " , OnKeyDown 被调用=" + bs.DownCalled
                + (bs.DownCalled ? (" eaten=" + bs.DownEaten) : ""));
            KeyEvent sp2 = SendKey(rig2.Context, VK_SPACE, log2);
            c.Obs("5b: buffer 清空后 SPACE : OnTestKeyDown eaten=" + sp2.TestEaten
                + " , OnKeyDown 被调用=" + sp2.DownCalled);
            Win32.Pump();
            c.Obs("5b: store 文本 = " + Describe(rig2.Store.Text));
            bool b_ok = !sp2.TestEaten;

            c.Passed = a_ok && b_ok;
            c.Verdict = "nima+BS×2+空格=\"" + actual + "\"(" + (a_ok ? "匹配预期" : "≠预期") + ")"
                + " ; n+BS 后空格不被吞=" + b_ok;
            DumpLog(c, log);
            c.Obs("---- 5b 事件日志 ----");
            for (int i = 0; i < log2.Count; i++) { c.Obs("  " + log2[i]); }
            return c;
        }

        // ===============================================================================
        // 用例 6：空缓冲区不吞功能键
        // ===============================================================================

        private static CaseResult Case6_EmptyBuffer(LockModel model)
        {
            CaseResult c = new CaseResult();
            c.Id = "用例6";
            c.Title = "空缓冲区（ActivateEx 之后立即）SPACE / RETURN / BACK 全部 eaten=false";

            List<string> log = new List<string>();
            Rig rig = NewRig(model, log);
            if (rig.SetupError != null)
            {
                c.Obs("建 context 失败: " + rig.SetupError);
                c.Verdict = "无法建立 ITfContext";
                c.Passed = false;
                return c;
            }
            c.Obs("注意：此刻 buffer 必然为空（ActivateEx 内部 ResetInput），且尚未敲过任何字母。");

            int[] vks = new int[] { VK_SPACE, VK_RETURN, VK_BACK };
            bool all = true;
            for (int i = 0; i < vks.Length; i++)
            {
                KeyEvent k = SendKey(rig.Context, vks[i], log);
                c.Obs("VK 0x" + vks[i].ToString("X2") + " (" + VkName(vks[i]) + ") : OnTestKeyDown eaten=" + k.TestEaten
                    + " hr=0x" + k.HrTest.ToString("X8") + " ; OnKeyDown 被调用=" + k.DownCalled);
                if (k.TestEaten) { all = false; }
            }
            c.Obs("store 文本 = " + Describe(rig.Store.Text));
            c.Passed = all;
            c.Verdict = all ? "三个功能键都没被吞" : "有功能键被误吞";
            return c;
        }

        // ===============================================================================
        // 用例 7：Ctrl / Alt 不吞
        // ===============================================================================

        private static CaseResult Case7_CtrlAlt()
        {
            CaseResult c = new CaseResult();
            c.Id = "用例7";
            c.Title = "Ctrl / Alt 按下时 N 必须 eaten=false";

            List<string> log = new List<string>();
            Rig rig = NewRig(LockModel.SyncGrant, log);
            if (rig.SetupError != null)
            {
                c.Obs("建 context 失败: " + rig.SetupError);
                c.Verdict = "无法建立 ITfContext";
                c.Passed = false;
                return c;
            }
            PrepareSut(rig.Context, log);
            c.Obs("[环境基线] " + ReleaseAllModifiers());

            try
            {
                // 基线：没有任何修饰键时 N 必须被吞，否则后面的「不吞」没有意义。
                KeyEvent baseline = SendKey(rig.Context, VK_N, log);
                c.Obs("基线（无修饰键）敲 N : OnTestKeyDown eaten=" + baseline.TestEaten + "（预期 true）");
                bool baselineOk = baseline.TestEaten;

                bool ctrlVerified, ctrlOk;
                string ctrlHow;
                ModifierProbe(rig, c, log, "Ctrl", VK_CONTROL, out ctrlVerified, out ctrlOk, out ctrlHow);

                bool altVerified, altOk;
                string altHow;
                ModifierProbe(rig, c, log, "Alt", VK_MENU, out altVerified, out altOk, out altHow);

                string ctrlText = ctrlVerified ? (ctrlOk ? "不吞" : "被吞") : "未验证";
                string altText = altVerified ? (altOk ? "不吞" : "被吞") : "未验证";

                // 判定：只要【验证到】的修饰键都表现为「不吞」就算过；
                // 环境注入不进去的那些记成「未验证」并在报告里如实说明，绝不冒充通过、也绝不冒充失败。
                bool anyVerified = ctrlVerified || altVerified;
                c.Passed = baselineOk && anyVerified && (!ctrlVerified || ctrlOk) && (!altVerified || altOk);
                c.Skipped = !anyVerified;
                c.Verdict = "基线被吞=" + baselineOk
                    + " ; Ctrl " + ctrlText + "（" + ctrlHow + "）"
                    + " ; Alt " + altText + "（" + altHow + "）";
            }
            finally
            {
                c.Obs("[收尾] " + ReleaseAllModifiers());
            }

            return c;
        }

        /// <summary>
        /// 对单个修饰键做一次「按下 → 敲 N → 松开」的注入与观测。
        /// 先试真实 SendInput；若本线程的 GetKeyState 没反映出来，再退到 SetKeyboardState
        /// （被测代码只读本线程 GetKeyState，所以两者对它的语义是等价的）。
        /// verified 为 false 表示**这个沙箱里注入不进去**，属「未验证」而非被测代码的问题。
        /// </summary>
        private static void ModifierProbe(Rig rig, CaseResult c, List<string> log, string name, int vk,
            out bool verified, out bool ok, out string how)
        {
            verified = false;
            ok = false;
            how = "未注入";

            // ---- 路径一：真实 SendInput ----
            try
            {
                ReleaseAllModifiers();

                INPUT[] down = new INPUT[1];
                down[0].type = Win32.INPUT_KEYBOARD;
                down[0].U.ki.wVk = (ushort)vk;
                down[0].U.ki.dwFlags = 0;
                uint sent = Win32.SendInput(1, down, Marshal.SizeOf(typeof(INPUT)));
                Win32.Pump();
                short st = Win32.GetKeyState(vk);
                bool downSeen = (st & 0x8000) != 0;
                c.Obs("SendInput 按下 " + name + " : 返回=" + sent + " , GetKeyState=0x" + ((ushort)st).ToString("X4")
                    + " 高位=" + downSeen);
                if (downSeen)
                {
                    KeyEvent k = SendKey(rig.Context, VK_N, log);
                    c.Obs("  → 按住 " + name + " 敲 N : OnTestKeyDown eaten=" + k.TestEaten + "（预期 false）");
                    verified = true;
                    ok = !k.TestEaten;
                    how = "SendInput";
                    return;
                }
            }
            catch (Exception ex)
            {
                c.Obs("SendInput 注入 " + name + " 抛异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                ReleaseAllModifiers();
            }

            // ---- 路径二：SetKeyboardState（只影响本线程，正是被测代码读的那张表）----
            try
            {
                byte[] st = new byte[256];
                if (!Win32.GetKeyboardState(st)) { how = "GetKeyboardState 失败"; return; }
                st[vk] = 0x80;
                bool set = Win32.SetKeyboardState(st);
                short gk = Win32.GetKeyState(vk);
                bool downSeen = (gk & 0x8000) != 0;
                c.Obs("SetKeyboardState 置 " + name + " : SetKeyboardState=" + set
                    + " , GetKeyState=0x" + ((ushort)gk).ToString("X4") + " 高位=" + downSeen);
                if (!downSeen) { how = "SetKeyboardState 无效"; return; }

                KeyEvent k = SendKey(rig.Context, VK_N, log);
                c.Obs("  → 按住 " + name + " 敲 N : OnTestKeyDown eaten=" + k.TestEaten + "（预期 false）");
                verified = true;
                ok = !k.TestEaten;
                how = "SetKeyboardState";

                byte[] clr = new byte[256];
                Win32.GetKeyboardState(clr);
                clr[vk] = 0x00;
                Win32.SetKeyboardState(clr);
                short after = Win32.GetKeyState(vk);
                c.Obs("  松开后 GetKeyState=0x" + ((ushort)after).ToString("X4"));
            }
            catch (Exception ex)
            {
                c.Obs("SetKeyboardState 注入 " + name + " 抛异常: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                ReleaseAllModifiers();
            }
        }

        // ===============================================================================
        // 用例 8：Shift 切换中/英
        // ===============================================================================

        private static CaseResult Case8_ShiftToggle()
        {
            CaseResult c = new CaseResult();
            c.Id = "用例8";
            c.Title = "单击 Shift（OnTestKeyUp→OnKeyUp）切换中/英模式";

            // ---------------------------------------------------------------------
            // 8a：全新实例上的基本切换
            // 实测时序（照 TextService.cs 的 _shiftPending/_shiftChord 状态机）：
            //   一次「单击 Shift」= OnTestKeyUp(VK_SHIFT) 紧跟 OnKeyUp(VK_SHIFT)，中间不夹任何其它键。
            //   全新实例里 _shiftChord=false，所以第 1 对就应该切换成功。
            // ---------------------------------------------------------------------
            {
                List<string> log = new List<string>();
                Rig rig = NewRig(LockModel.SyncGrant, log);
                if (rig.SetupError != null)
                {
                    c.Obs("8a 建 context 失败: " + rig.SetupError);
                    c.Verdict = "无法建立 ITfContext";
                    c.Passed = false;
                    return c;
                }
                PrepareSut(rig.Context, log);

                bool before = ProbeChinese(rig.Context, log);
                c.Obs("8a 切换前：敲 N 被吞（=中文模式）= " + before
                    + "（全新实例，_chineseMode 初值 true、_shiftChord=false）");

                ShiftPair(rig.Context, log, "第1对");
                bool after = ProbeChinese(rig.Context, log);
                c.Obs("8a 第 1 对 OnTestKeyUp(VK_SHIFT)+OnKeyUp(VK_SHIFT) 后：敲 N 被吞 = " + after
                    + "（预期 false，即已切到英文）");

                Win32.Pump();

                ShiftPair(rig.Context, log, "第2对");
                bool back = ProbeChinese(rig.Context, log);
                c.Obs("8a 第 2 对后：敲 N 被吞 = " + back + "（预期 true，即切回中文）");

                c.Obs("8a 实际使用时序 = OnTestKeyUp(VK_SHIFT) → OnKeyUp(VK_SHIFT)，两者之间不调用任何其它入口");
                c.Obs("8a 结论：中文=" + before + " → 英文=" + (!after) + " → 中文=" + back);
                c.Passed = before && !after && back;
                c.Verdict = "中文=" + before + " → 切换后英文=" + (!after) + " → 再切回中文=" + back;
                DumpLog(c, log);
            }

            // ---------------------------------------------------------------------
            // 8b：_shiftChord 闩锁效应（真实存在的可疑行为，单独测）
            // TextService.cs 的 OnKeyDown 对任何「非字母但被吞」的键都会 _shiftChord = true；
            // 而只有 OnKeyUp(VK_SHIFT) / ResetInput() 会清它。退格把缓冲区清空时
            // Backspace() 走的是「空 → _candidates=null; HidePanel(); return;」这条捷径，
            // 不经过 ResetInput()，于是 _shiftChord 残留为 true。
            // 后果：紧接着的一次 Shift 单击会被吞掉、不切换，用户必须按第二次。
            // ---------------------------------------------------------------------
            {
                List<string> log = new List<string>();
                Rig rig = NewRig(LockModel.SyncGrant, log);
                if (rig.SetupError != null)
                {
                    c.Obs("8b 建 context 失败: " + rig.SetupError);
                    c.Passed = false;
                    return c;
                }

                bool start = ProbeChinese(rig.Context, log);
                c.Obs("8b 起点：中文模式 = " + start);

                KeyEvent kn = SendKey(rig.Context, VK_N, log);
                KeyEvent kbs = SendKey(rig.Context, VK_BACK, log);
                c.Obs("8b 敲 n（eaten=" + kn.TestEaten + "）再退格（eaten=" + kbs.TestEaten
                    + "）：缓冲区已被退格清空，但 OnKeyDown 里 _shiftChord 已被置 true 且无人清除");

                ShiftPair(rig.Context, log, "8b-第1对");
                bool t1 = ProbeChinese(rig.Context, log);
                c.Obs("8b 第 1 对 Shift 后：敲 N 被吞 = " + t1 + "（若仍为 true，说明这一次单击被闩锁吞掉了）");

                ShiftPair(rig.Context, log, "8b-第2对");
                bool t2 = ProbeChinese(rig.Context, log);
                c.Obs("8b 第 2 对 Shift 后：敲 N 被吞 = " + t2 + "（预期 false，第 2 次才切换）");

                c.Obs("8b 判定：" + (t1
                    ? "复现了『退格清空缓冲区后，第一次 Shift 单击无效』"
                    : "未复现闩锁效应（第 1 对就切换了）"));
                DumpLog(c, log);
            }

            return c;
        }

        // ===============================================================================
        // 用例 9：候选浮窗不崩 + 截图
        // ===============================================================================

        private static CaseResult Case9_Panel()
        {
            CaseResult c = new CaseResult();
            c.Id = "用例9";
            c.Title = "候选浮窗 CandidatePanel 全程参与不崩 + 截屏 panel.png";

            List<string> log = new List<string>();
            Rig rig = NewRig(LockModel.SyncGrant, log);
            if (rig.SetupError != null)
            {
                c.Obs("建 context 失败: " + rig.SetupError);
                c.Verdict = "无法建立 ITfContext";
                c.Passed = false;
                return c;
            }
            PrepareSut(rig.Context, log);

            // 让面板有东西可显示：逐个字母输入，每次都会 Show
            TypeKeys(rig.Context, new int[] { VK_N }, log);
            Win32.Pump();
            TypeKeys(rig.Context, new int[] { VK_I }, log);
            Win32.Pump();
            TypeKeys(rig.Context, new int[] { VK_M }, log);
            Win32.Pump();
            TypeKeys(rig.Context, new int[] { VK_A }, log);
            Win32.Pump();

            IntPtr hwndPanel = Win32.FindWindowW(PanelClassName, null);
            c.Obs("FindWindowW(\"" + PanelClassName + "\") = 0x" + hwndPanel.ToInt64().ToString("X"));
            if (hwndPanel == IntPtr.Zero)
            {
                c.Obs("→ 面板窗口不存在，无法截图。");
            }
            else
            {
                int ex = Win32.GetWindowLongW(hwndPanel, Win32.GWL_EXSTYLE);
                c.Obs("面板 EX 样式 = 0x" + ex.ToString("X8")
                    + " (TOOLWINDOW=" + ((ex & 0x80) != 0)
                    + ", TOPMOST=" + ((ex & 0x8) != 0)
                    + ", NOACTIVATE=" + ((ex & 0x08000000) != 0) + ")");
                StringBuilder cls = new StringBuilder(256);
                Win32.GetClassNameW(hwndPanel, cls, 256);
                StringBuilder title = new StringBuilder(256);
                Win32.GetWindowTextW(hwndPanel, title, 256);
                c.Obs("class=\"" + cls + "\" title=\"" + title + "\"");
                c.Obs("IsWindowVisible=" + Win32.IsWindowVisible(hwndPanel));
            }

            string png = Path.Combine(OutDir, "panel.png");
            bool captured = false;
            if (hwndPanel != IntPtr.Zero)
            {
                captured = CaptureWindow(hwndPanel, png, c, log);
            }
            c.Obs("截图结果: " + (captured ? "已保存 " + png : "未抓到"));
            c.Obs("进程仍存活 = true（若本用例返回则说明没崩）");

            // 让面板 Hide 一次，验证正常收尾
            KeyEvent esc = SendKey(rig.Context, VK_ESCAPE, log);
            Win32.Pump();
            c.Obs("ESC 收起面板 : OnTestKeyDown eaten=" + esc.TestEaten);
            c.Obs("ESC 之后 IsWindowVisible=" + (hwndPanel == IntPtr.Zero ? "n/a" : Win32.IsWindowVisible(hwndPanel).ToString()));

            c.Passed = true; // 只要走到这里没崩就算过
            c.Verdict = captured ? "面板正常，已截屏" : "面板正常，但截屏未成功";
            DumpLog(c, log);
            return c;
        }

        private static bool CaptureWindow(IntPtr hwnd, string path, CaseResult c, List<string> log)
        {
            RECT rc;
            if (!Win32.GetWindowRect(hwnd, out rc))
            {
                c.Obs("GetWindowRect 失败 err=" + Marshal.GetLastWin32Error());
                return false;
            }
            int w = rc.right - rc.left;
            int h = rc.bottom - rc.top;
            c.Obs("GetWindowRect = (" + rc.left + "," + rc.top + ")-(" + rc.right + "," + rc.bottom + ") 尺寸 " + w + "x" + h);
            if (w <= 0 || h <= 0)
            {
                c.Obs("窗口尺寸非正，无法截图");
                return false;
            }

            // 让面板真的可见并在最前（Panel 自己是 WS_EX_NOACTIVATE|TOPMOST，不会抢焦点）
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, rc.left, rc.top, w, h,
                Win32.SWP_SHOWWINDOW | Win32.SWP_NOACTIVATE);
            Win32.ShowWindow(hwnd, Win32.SW_SHOWNOACTIVATE);
            Win32.InvalidateRect(hwnd, IntPtr.Zero, true);
            Win32.UpdateWindow(hwnd);
            Win32.Pump();
            Thread.Sleep(150);
            Win32.Pump();
            c.Obs("强制显示后 IsWindowVisible=" + Win32.IsWindowVisible(hwnd));

            IntPtr hdcScreen = Win32.GetDC(IntPtr.Zero);
            IntPtr hdcMem = Win32.CreateCompatibleDC(hdcScreen);
            IntPtr hBmp = Win32.CreateCompatibleBitmap(hdcScreen, w, h);
            IntPtr old = Win32.SelectObject(hdcMem, hBmp);

            // 先试 PrintWindow（面板可能不实现 WM_PRINTCLIENT）
            bool pw = Win32.PrintWindow(hwnd, hdcMem, Win32.PW_CLIENTONLY);
            int pwNonBlack = CountNonBlack(hBmp, w, h);
            c.Obs("PrintWindow -> " + pw + " , 非黑像素=" + pwNonBlack);

            // 再用屏幕 BitBlt 抓真实像素
            bool bb = Win32.BitBlt(hdcMem, 0, 0, w, h, hdcScreen, rc.left, rc.top, Win32.SRCCOPY);
            int bbNonBlack = CountNonBlack(hBmp, w, h);
            c.Obs("BitBlt(屏幕 DC) -> " + bb + " , 非黑像素=" + bbNonBlack);

            bool ok = false;
            if (pwNonBlack > 0 || bbNonBlack > 0)
            {
                Win32.SelectObject(hdcMem, old);
                try
                {
                    using (Bitmap bmp = Image.FromHbitmap(hBmp))
                    {
                        bmp.Save(path, ImageFormat.Png);
                        ok = true;
                        c.Obs("已保存 PNG: " + path + " (" + new FileInfo(path).Length + " 字节)");
                        c.Obs("PNG 尺寸 = " + bmp.Width + "x" + bmp.Height);
                    }
                }
                catch (Exception ex)
                {
                    c.Obs("保存 PNG 失败: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            else
            {
                c.Obs("两种方式都抓到全黑像素（本会话可能没有可访问的交互式桌面）");
                Win32.SelectObject(hdcMem, old);
            }

            Win32.DeleteObject(hBmp);
            Win32.DeleteDC(hdcMem);
            Win32.ReleaseDC(IntPtr.Zero, hdcScreen);
            return ok;
        }

        private static int CountNonBlack(IntPtr hBmp, int w, int h)
        {
            try
            {
                using (Bitmap bmp = Image.FromHbitmap(hBmp))
                {
                    int n = 0;
                    int stepX = Math.Max(1, w / 60);
                    int stepY = Math.Max(1, h / 30);
                    for (int y = 0; y < h; y += stepY)
                    {
                        for (int x = 0; x < w; x += stepX)
                        {
                            Color px = bmp.GetPixel(x, y);
                            if (px.R > 8 || px.G > 8 || px.B > 8) { n++; }
                        }
                    }
                    return n;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // ===============================================================================
        // 用例 10：异常安全（pic = null）
        // ===============================================================================

        private static CaseResult Case10_NullContext()
        {
            CaseResult c = new CaseResult();
            c.Id = "用例10";
            c.Title = "OnKeyDown(pic=null) / OnTestKeyDown(pic=null) 必须返回 S_OK 且不抛异常";

            // 用专用实例：pic=null 会让状态机停在异常路径上（OnKeyDown(VK_N) 会把 'n' 追加进缓冲区、
            // 再把 UpdatePanel 之类的调用丢进 catch），不该让它污染任何真实用例的实例。
            _svc = new TextService();
            _svc.ActivateEx(_ptim, _tid, 0);
            AllSvcs.Add(_svc);
            c.Obs("本用例使用独立 TextService 实例（第 " + AllSvcs.Count + " 个）");

            bool threw = false;
            string thr = "";
            int hrTest = -1, hrDown = -1;
            bool eaten = true;

            try
            {
                eaten = true;
                hrTest = _svc.OnTestKeyDown(null, (IntPtr)VK_N, KeyLParam, out eaten);
                c.Obs("OnTestKeyDown(null, VK_N) -> hr=0x" + hrTest.ToString("X8") + " eaten=" + eaten);
            }
            catch (Exception ex)
            {
                threw = true;
                thr = "OnTestKeyDown 抛了 " + ex.GetType().FullName + ": " + ex.Message;
            }

            try
            {
                eaten = false;
                hrDown = _svc.OnKeyDown(null, (IntPtr)VK_N, KeyLParam, out eaten);
                c.Obs("OnKeyDown(null, VK_N) -> hr=0x" + hrDown.ToString("X8") + " eaten=" + eaten);
            }
            catch (Exception ex)
            {
                threw = true;
                thr += " OnKeyDown 抛了 " + ex.GetType().FullName + ": " + ex.Message;
            }

            // 再补一发：空格（会走 CommitSelected → InsertIntoDocument(null,...) 返回 false 的分支）
            try
            {
                eaten = false;
                int hr3 = _svc.OnKeyDown(null, (IntPtr)VK_SPACE, KeyLParam, out eaten);
                c.Obs("OnKeyDown(null, VK_SPACE) -> hr=0x" + hr3.ToString("X8") + " eaten=" + eaten);
            }
            catch (Exception ex)
            {
                threw = true;
                thr += " OnKeyDown(null, VK_SPACE) 抛了 " + ex.GetType().FullName + ": " + ex.Message;
            }

            // 收尾：把状态清掉，别影响后面的对照实验
            List<string> log = new List<string>();
            Rig rig = NewRig(LockModel.SyncGrant, log);
            if (rig.SetupError == null)
            {
                while (true)
                {
                    bool e;
                    if (_svc.OnTestKeyDown(rig.Context, (IntPtr)VK_BACK, KeyLParam, out e) != 0 || !e) { break; }
                    bool e2;
                    _svc.OnKeyDown(rig.Context, (IntPtr)VK_BACK, KeyLParam, out e2);
                }
            }

            c.Passed = !threw && hrTest == 0 && hrDown == 0;
            c.Verdict = threw ? ("抛异常: " + thr) : ("无异常逃逸，hr 均为 S_OK (0x" + hrTest.ToString("X8") + " / 0x" + hrDown.ToString("X8") + ")");
            return c;
        }

        // ===============================================================================
        // 附加实验：两种 RequestLock 模型
        // ===============================================================================

        private static CaseResult Experiment_LockModelA()
        {
            CaseResult c = new CaseResult();
            c.Id = "实验A";
            c.Title = "RequestLock 模型 A（同步授权：RequestLock 内回调 OnLockGranted）";

            List<string> log = new List<string>();
            Rig rig = NewRig(LockModel.SyncGrant, log);
            if (rig.SetupError != null)
            {
                c.Obs("建 context 失败: " + rig.SetupError);
                c.Verdict = "无法建立 ITfContext";
                c.Passed = false;
                return c;
            }
            PrepareSut(rig.Context, log);
            TypeKeys(rig.Context, new int[] { VK_N, VK_I, VK_M, VK_A }, log);
            SendKey(rig.Context, VK_SPACE, log);
            Win32.Pump();

            c.Obs("RequestLock 调用次数 = " + rig.Store.RequestLockCalls);
            c.Obs("OnLockGranted 调用次数 = " + rig.Store.OnLockGrantedCalls);
            c.Obs("InsertTextAtSelection 调用次数 = " + rig.Store.InsertTextAtSelectionCalls);
            c.Obs("最后一次锁标志 = 0x" + rig.Store.LastLockFlags.ToString("X")
                + " (TS_LF_SYNC=" + ((rig.Store.LastLockFlags & Ts.TS_LF_SYNC) != 0)
                + ", TS_LF_READWRITE=" + ((rig.Store.LastLockFlags & Ts.TS_LF_READWRITE) == Ts.TS_LF_READWRITE) + ")");
            c.Obs("★ store 文本 = " + Describe(rig.Store.Text));
            c.Passed = string.Equals(rig.Store.Text, ExpectedMain, StringComparison.Ordinal);
            c.Verdict = c.Passed ? "链路跑通，文本正确" : "文本=" + Describe(rig.Store.Text);
            DumpLog(c, log);
            return c;
        }

        private static CaseResult Experiment_LockModelB()
        {
            CaseResult c = new CaseResult();
            c.Id = "实验B";
            c.Title = "RequestLock 模型 B（延迟授权：*phrSession=TS_S_ASYNC，宿主稍后自己回调 OnLockGranted）";

            List<string> log = new List<string>();
            Rig rig = NewRig(LockModel.DeferredGrant, log);
            if (rig.SetupError != null)
            {
                c.Obs("建 context 失败: " + rig.SetupError);
                c.Verdict = "无法建立 ITfContext";
                c.Passed = false;
                return c;
            }
            PrepareSut(rig.Context, log);
            TypeKeys(rig.Context, new int[] { VK_N, VK_I, VK_M, VK_A }, log);

            KeyEvent space = SendKey(rig.Context, VK_SPACE, log);
            c.Obs("SPACE : OnTestKeyDown eaten=" + space.TestEaten + " , OnKeyDown 被调用=" + space.DownCalled);
            Win32.Pump();

            c.Obs("RequestEditSession（在 OnKeyDown 内部）返回后：");
            c.Obs("  RequestLock 调用次数 = " + rig.Store.RequestLockCalls);
            c.Obs("  OnLockGranted 调用次数 = " + rig.Store.OnLockGrantedCalls + "（模型 B 下宿主还没授权，预期 0）");
            c.Obs("  HasDeferredLock = " + rig.Store.HasDeferredLock);
            c.Obs("  InsertTextAtSelection 次数 = " + rig.Store.InsertTextAtSelectionCalls);
            c.Obs("  store 文本 = " + Describe(rig.Store.Text));
            c.Obs("  面板是否仍显示 = " + (Win32.FindWindowW(PanelClassName, null) != IntPtr.Zero ? "窗口存在" : "窗口不存在")
                + " , IsWindowVisible=" + Win32.IsWindowVisible(Win32.FindWindowW(PanelClassName, null)));

            bool ran = rig.Store.RunDeferredLock();
            Win32.Pump();
            c.Obs("宿主补调 RunDeferredLock() -> " + ran);
            c.Obs("  OnLockGranted 调用次数 = " + rig.Store.OnLockGrantedCalls);
            c.Obs("  InsertTextAtSelection 次数 = " + rig.Store.InsertTextAtSelectionCalls);
            c.Obs("  ★ 补授权之后 store 文本 = " + Describe(rig.Store.Text));

            bool empty = rig.Store.TextLength == 0;
            c.Obs("");
            c.Obs("结论：模型 B 下文本" + (empty ? "【没有】" : "有") + "进到存储里。");
            c.Obs("原因（读 TextService.cs 的 InsertIntoDocument 得到）：RequestEditSession 一返回，"
                + "它就把 _pendingInsert/_pendingContext 清成了 null，_pendingInsertHr 仍是 E_FAIL，"
                + "于是 CommitSelected 认为提交失败；等宿主稍后补授权时 DoEditSession 看到的 _pendingInsert 已经是 null，"
                + "直接返回 S_OK 什么都没写。也就是说 TextService 假设 RequestEditSession 一定是“同会话内同步完成”的，"
                + "这对异步锁（TF_ES_ASYNC / TS_S_ASYNC）是错的——只是它自己从不请求异步锁，所以线上不会触发。");
            // 本实验“通过”的含义是：模型 B 失败（与模型 A 形成对照）。
            // Passed = (文本没进存储) == 预期成立；ExpectedFail 让汇总把它标成 EXPECTED-FAIL 而不是 PASS。
            c.Passed = empty;
            c.ExpectedFail = true;
            c.Verdict = empty ? "如预期：模型 B 无法把文本写进存储" : "意外：模型 B 竟然写成功了";
            DumpLog(c, log);
            return c;
        }

        private static CaseResult Extra_ActivateEntry()
        {
            CaseResult c = new CaseResult();
            c.Id = "附加";
            c.Title = "ITfTextInputProcessor::Activate 入口（而非 ActivateEx）";

            List<string> log = new List<string>();
            Rig rig = NewRig(LockModel.SyncGrant, log, false);
            if (rig.SetupError != null)
            {
                c.Obs("建 context 失败: " + rig.SetupError);
                c.Verdict = "无法建立 ITfContext";
                c.Passed = false;
                return c;
            }

            bool eatenProbe;
            _svc.OnTestKeyDown(rig.Context, (IntPtr)VK_N, KeyLParam, out eatenProbe);
            c.Obs("Activate 之前先探一下：OnTestKeyDown(VK_N) eaten=" + eatenProbe + "（预期 false，_active 还是 false）");

            int hr = _svc.Activate(_ptim, _tid);
            c.Obs("Activate(ptim, tid) -> hr=0x" + hr.ToString("X8"));
            bool eaten;
            int hrT = _svc.OnTestKeyDown(rig.Context, (IntPtr)VK_N, KeyLParam, out eaten);
            c.Obs("随后 OnTestKeyDown(VK_N) -> hr=0x" + hrT.ToString("X8") + " eaten=" + eaten
                + "（预期 true，说明 Activate 也把 _active 置上了）");

            TypeKeys(rig.Context, new int[] { VK_N, VK_I, VK_M, VK_A }, log);
            SendKey(rig.Context, VK_SPACE, log);
            Win32.Pump();
            c.Obs("nima+空格 → store 文本 = " + Describe(rig.Store.Text));

            c.Passed = eaten && string.Equals(rig.Store.Text, ExpectedMain, StringComparison.Ordinal);
            c.Verdict = c.Passed ? "Activate 入口同样可用" : "Activate 之后行为异常";
            return c;
        }

        // ===============================================================================
        // 通用工具
        // ===============================================================================

        private static Rig NewRig(LockModel model, List<string> log)
        {
            return NewRig(model, log, true);
        }

        /// <param name="activateEx">
        /// false 表示只建实例、不调 ActivateEx —— 留给 Extra 那条要单独验证 Activate 入口的用例。
        /// </param>
        private static Rig NewRig(LockModel model, List<string> log, bool activateEx)
        {
            Rig rig = new Rig();
            FakeTextStore store = new FakeTextStore();
            store.Hwnd = _hwndHost;
            store.Model = model;
            rig.Store = store;

            // 每个 rig 配一个全新的 TextService 实例。
            // 被测对象的中/英文模式（_chineseMode）、_buffer、_shiftChord 这些状态都挂在实例上，
            // 首轮实测里共用一个实例导致用例之间互相污染：用例 8 把实例切到英文模式后，
            // 用例 9 / 实验 A / 实验 B / Extra 全部跟着失败。一条用例一个实例，
            // 既消除污染，也更贴近真实使用（一个线程一个 TSP 实例）。
            _svc = new TextService();
            int hrNew = activateEx ? _svc.ActivateEx(_ptim, _tid, 0) : -1;
            AllSvcs.Add(_svc);
            log.Add("[rig] new TextService()" + (activateEx ? " + ActivateEx" : "（未激活，留给本用例自己调入口）")
                + " -> hr=0x" + hrNew.ToString("X8")
                + "（本进程累计实例数 " + AllSvcs.Count + "）");

            ITfDocumentMgr pdim;
            int hr = _ptim.CreateDocumentMgr(out pdim);
            if (hr != 0 || pdim == null)
            {
                rig.SetupError = "CreateDocumentMgr hr=0x" + hr.ToString("X8");
                log.Add("[rig] " + rig.SetupError);
                return rig;
            }
            rig.DocMgr = pdim;

            ITfContext pic;
            uint ec;
            hr = pdim.CreateContext(_tid, 0, store, out pic, out ec);
            if (hr != 0 || pic == null)
            {
                rig.SetupError = "CreateContext hr=0x" + hr.ToString("X8") + " pic=" + (pic == null ? "null" : "OK");
                log.Add("[rig] " + rig.SetupError);
                return rig;
            }
            rig.Context = pic;
            rig.TextStoreEc = ec;
            log.Add("[rig] CreateContext 成功 ecTextStore=" + ec);

            hr = pdim.Push(pic);
            log.Add("[rig] Push hr=0x" + hr.ToString("X8"));
            if (hr != 0)
            {
                rig.SetupError = "Push hr=0x" + hr.ToString("X8");
            }
            return rig;
        }

        /// <summary>
        /// 把被测对象恢复到「中文模式 + 空缓冲区」。每个 rig 都有全新实例，正常情况下这里是空操作，
        /// 只做一次诊断性探测。**绝不主动敲 Shift**：首轮实测里那段「敲两对 Shift 切回中文」的代码
        /// 因为不知道当前 _shiftChord 状态，两次切换互相抵消，反而把实例留在英文模式，
        /// 污染了后面的用例 9 / 实验 A / 实验 B / Extra。
        /// </summary>
        private static void PrepareSut(ITfContext pic, List<string> log)
        {
            // 0) 修饰键是**进程/线程级**状态，不是每个 rig 各自的：上一轮用例（尤其是真实
            //    SendInput 注入 Ctrl/Alt 的那些）如果没把状态清干净，后面所有 rig 的 ShouldEat
            //    都会因为 HasControlOrAlt() 为真而恒返回 false —— 表现为「字母不被吞、文本为空」。
            //    所以每个 rig 起步先观测、再强制清零，避免跨用例污染。
            string mods = ClearStuckModifiers();
            if (mods != null) { log.Add("[prepare] " + mods); }

            // 1) 清 buffer：退格只在 buffer 非空时被吞，所以吞不动了就是空了。
            int guard = 0;
            while (guard++ < 64)
            {
                bool e;
                int hr = _svc.OnTestKeyDown(pic, (IntPtr)VK_BACK, KeyLParam, out e);
                if (hr != 0 || !e) { break; }
                bool e2;
                _svc.OnKeyDown(pic, (IntPtr)VK_BACK, KeyLParam, out e2);
            }
            log.Add("[prepare] buffer 已清空（退格 " + guard + " 次内收敛）");

            // 2) 只探测、不纠正：rig 是新实例，_chineseMode 必然为 true。
            log.Add("[prepare] 当前敲 N 被吞（=中文模式）= " + ProbeChinese(pic, log));
        }

        private static bool ProbeChinese(ITfContext pic, List<string> log)
        {
            bool e;
            _svc.OnTestKeyDown(pic, (IntPtr)VK_N, KeyLParam, out e);
            return e;
        }

        /// <summary>
        /// 主动把 Ctrl/Alt/Shift 的物理按键「松开」再清线程状态表。
        /// 必须做：SendInput 注入的是**系统级**按键事件，一旦按下没松开，状态会跨用例、
        /// 甚至跨进程残留（实测：上一次运行注入的修饰键状态让下一次运行的全部用例都失败）。
        /// </summary>
        private static string ReleaseAllModifiers()
        {
            string sendInfo;
            try
            {
                INPUT[] ups = new INPUT[6];
                int[] vks = new int[] { VK_CONTROL, VK_MENU, VK_SHIFT, VK_LSHIFT, VK_RSHIFT, VK_MENU };
                for (int i = 0; i < ups.Length; i++)
                {
                    ups[i].type = Win32.INPUT_KEYBOARD;
                    ups[i].U.ki.wVk = (ushort)vks[i];
                    ups[i].U.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
                }
                uint sent = Win32.SendInput((uint)ups.Length, ups, Marshal.SizeOf(typeof(INPUT)));
                Win32.Pump();
                Win32.keybd_event((byte)VK_CONTROL, 0, Win32.KEYEVENTF_KEYUP, IntPtr.Zero);
                Win32.keybd_event((byte)VK_MENU, 0, Win32.KEYEVENTF_KEYUP, IntPtr.Zero);
                sendInfo = "SendInput 松开 6 个修饰键 → 返回 " + sent;
            }
            catch (Exception ex)
            {
                sendInfo = "SendInput 松键异常: " + ex.Message;
            }
            return sendInfo + " ; " + ClearStuckModifiers();
        }

        /// <summary>
        /// 这是必要的：真实 SendInput 一旦注入成功，修饰键状态会一直留在进程里，
        /// 而 TextService.ShouldEat() 开头就是 HasControlOrAlt() → 为真则永远不吞键。
        /// </summary>
        private static string ClearStuckModifiers()
        {
            try
            {
                byte[] s = new byte[256];
                if (!Win32.GetKeyboardState(s)) { return "GetKeyboardState 失败，未能清理修饰键"; }

                short beforeCtrl = Win32.GetKeyState(VK_CONTROL);
                short beforeAlt = Win32.GetKeyState(VK_MENU);
                short beforeShift = Win32.GetKeyState(VK_SHIFT);

                bool dirty = (beforeCtrl & 0x8000) != 0 || (beforeAlt & 0x8000) != 0
                    || (beforeShift & 0x8000) != 0 || s[VK_CONTROL] != 0 || s[VK_MENU] != 0;

                s[VK_CONTROL] = 0x00;
                s[VK_MENU] = 0x00;
                s[VK_SHIFT] = 0x00;
                s[VK_LSHIFT] = 0x00;
                s[VK_RSHIFT] = 0x00;
                Win32.SetKeyboardState(s);

                short afterCtrl = Win32.GetKeyState(VK_CONTROL);
                short afterAlt = Win32.GetKeyState(VK_MENU);

                return "修饰键清理：清前 Ctrl=0x" + ((ushort)beforeCtrl).ToString("X4")
                    + " Alt=0x" + ((ushort)beforeAlt).ToString("X4")
                    + " Shift=0x" + ((ushort)beforeShift).ToString("X4")
                    + (dirty ? "（脏）" : "（干净）")
                    + " → 清后 Ctrl=0x" + ((ushort)afterCtrl).ToString("X4")
                    + " Alt=0x" + ((ushort)afterAlt).ToString("X4");
            }
            catch (Exception ex)
            {
                return "修饰键清理异常: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static void ShiftPair(ITfContext pic, List<string> log, string tag)
        {
            bool e1, e2;
            int h1 = _svc.OnTestKeyUp(pic, (IntPtr)VK_SHIFT, KeyLParam, out e1);
            int h2 = _svc.OnKeyUp(pic, (IntPtr)VK_SHIFT, KeyLParam, out e2);
            log.Add("[shift " + tag + "] OnTestKeyUp(VK_SHIFT) hr=0x" + h1.ToString("X8") + " eaten=" + e1
                + " ; OnKeyUp(VK_SHIFT) hr=0x" + h2.ToString("X8") + " eaten=" + e2);
        }

        private static List<KeyEvent> TypeKeys(ITfContext pic, int[] vks, List<string> log)
        {
            List<KeyEvent> list = new List<KeyEvent>();
            for (int i = 0; i < vks.Length; i++)
            {
                list.Add(SendKey(pic, vks[i], log));
            }
            return list;
        }

        /// <summary>严格执行真实 TSF 的行为：OnTestKeyDown 返回 false 就绝不再调 OnKeyDown。</summary>
        private static KeyEvent SendKey(ITfContext pic, int vk, List<string> log)
        {
            KeyEvent k = new KeyEvent();
            k.Vk = vk;
            bool e;
            k.HrTest = _svc.OnTestKeyDown(pic, (IntPtr)vk, KeyLParam, out e);
            k.TestEaten = e;
            if (e)
            {
                k.DownCalled = true;
                bool e2;
                k.HrDown = _svc.OnKeyDown(pic, (IntPtr)vk, KeyLParam, out e2);
                k.DownEaten = e2;
            }
            log.Add("[key] " + VkName(vk) + " testEaten=" + k.TestEaten + " hrTest=0x" + k.HrTest.ToString("X8")
                + " downCalled=" + k.DownCalled
                + (k.DownCalled ? (" downEaten=" + k.DownEaten + " hrDown=0x" + k.HrDown.ToString("X8")) : ""));
            return k;
        }

        private static string VkName(int vk)
        {
            switch (vk)
            {
                case VK_BACK: return "VK_BACK(0x08)";
                case VK_RETURN: return "VK_RETURN(0x0D)";
                case VK_SHIFT: return "VK_SHIFT(0x10)";
                case VK_CONTROL: return "VK_CONTROL(0x11)";
                case VK_MENU: return "VK_MENU(0x12)";
                case VK_ESCAPE: return "VK_ESCAPE(0x1B)";
                case VK_SPACE: return "VK_SPACE(0x20)";
                case VK_N: return "'N'";
                case VK_I: return "'I'";
                case VK_M: return "'M'";
                case VK_A: return "'A'";
                case VK_1: return "'1'";
                case VK_2: return "'2'";
                default: return "VK(0x" + vk.ToString("X2") + ")";
            }
        }

        /// <summary>同时给出字符串和逐字符码点，避免任何编码层面的歧义。</summary>
        private static string Describe(string s)
        {
            if (s == null) { return "<null>"; }
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            sb.Append(s);
            sb.Append("\" (len=");
            sb.Append(s.Length);
            sb.Append(", 码点=");
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0) { sb.Append(' '); }
                sb.Append("U+");
                sb.Append(((int)s[i]).ToString("X4"));
            }
            sb.Append(")");
            return sb.ToString();
        }

        private static void DumpLog(CaseResult c, List<string> log)
        {
            if (log.Count == 0) { return; }
            c.Obs("---- 假文本存储事件日志 ----");
            for (int i = 0; i < log.Count; i++) { c.Obs("  " + log[i]); }
        }

        private static IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private static IntPtr CreateHostWindow()
        {
            string cls = "Xingbian.E2E.HostWindow";
            Win32.WNDCLASSEX wc = new Win32.WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(Win32.WNDCLASSEX));
            wc.style = 0;
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_hostProc);
            wc.cbClsExtra = 0;
            wc.cbWndExtra = 0;
            wc.hInstance = Win32.GetModuleHandleW(null);
            wc.hIcon = IntPtr.Zero;
            wc.hCursor = IntPtr.Zero;
            wc.hbrBackground = IntPtr.Zero;
            wc.lpszMenuName = null;
            wc.lpszClassName = cls;
            wc.hIconSm = IntPtr.Zero;
            ushort atom = Win32.RegisterClassExW(ref wc);
            if (atom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS
                {
                    Say("[host] RegisterClassExW 失败 err=" + err);
                }
            }
            return Win32.CreateWindowExW(0, cls, "Xingbian E2E Host", Win32.WS_POPUP,
                0, 0, 40, 20, IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        }

        // ===============================================================================
        // 输出
        // ===============================================================================

        private static void Say(string s)
        {
            Console2.Add(s);
            Console.WriteLine(s);
        }

        private static void WriteArtifacts()
        {
            if (!Directory.Exists(OutDir)) { Directory.CreateDirectory(OutDir); }

            UTF8Encoding utf8 = new UTF8Encoding(false);
            File.WriteAllLines(Path.Combine(OutDir, "run-output.txt"), Console2.ToArray(), utf8);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 端到端验证 harness 结果（由程序自动生成）");
            sb.AppendLine();
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            sb.AppendLine("本文件的用例观测值由 `E2E.exe` 运行时直接写出，未经人工改写。");
            sb.AppendLine("（分析结论、RequestLock 双模型对比、ITextStoreACP 方法序核对证据见文件末尾的手写章节。）");
            sb.AppendLine();
            sb.AppendLine("## 运行环境");
            sb.AppendLine();
            sb.AppendLine("```");
            int envEnd = 0;
            for (int i = 0; i < Console2.Count; i++)
            {
                if (Console2[i].StartsWith("================", StringComparison.Ordinal)) { envEnd = i; break; }
            }
            for (int i = 0; i <= envEnd && i < Console2.Count; i++) { sb.AppendLine(Console2[i]); }
            sb.AppendLine("```");
            sb.AppendLine();

            int pass = 0, fail = 0, skip = 0, xfail = 0;
            for (int i = 0; i < Results.Count; i++)
            {
                if (Results[i].Skipped) { skip++; }
                else if (Results[i].ExpectedFail) { xfail++; }
                else if (Results[i].Passed) { pass++; }
                else { fail++; }
            }
            sb.AppendLine("## 汇总");
            sb.AppendLine();
            sb.AppendLine("| 用例 | 结论 | 判定 |");
            sb.AppendLine("| --- | --- | --- |");
            for (int i = 0; i < Results.Count; i++)
            {
                CaseResult r = Results[i];
                string tag;
                if (r.Skipped) { tag = "SKIP"; }
                else if (r.ExpectedFail) { tag = r.Passed ? "**EXPECTED-FAIL**" : "**UNEXPECTED-PASS**"; }
                else { tag = r.Passed ? "**PASS**" : "**FAIL**"; }
                sb.AppendLine("| " + r.Id + " " + r.Title + " | " + r.Verdict.Replace("|", "\\|") + " | " + tag + " |");
            }
            sb.AppendLine();
            sb.AppendLine("PASS=" + pass + " FAIL=" + fail + " SKIP=" + skip
                + " ；另有对照实验 " + xfail + " 条，按设计预期就是失败（EXPECTED-FAIL），不计入 FAIL。");
            sb.AppendLine();
            sb.AppendLine("## 逐条用例的实际观测值");
            sb.AppendLine();
            for (int i = 0; i < Results.Count; i++)
            {
                CaseResult r = Results[i];
                sb.AppendLine("### " + r.Id + " — " + r.Title);
                sb.AppendLine();
                string verdictTag;
                if (r.Skipped) { verdictTag = "SKIP"; }
                else if (r.ExpectedFail) { verdictTag = r.Passed ? "EXPECTED-FAIL" : "UNEXPECTED-PASS"; }
                else { verdictTag = r.Passed ? "PASS" : "FAIL"; }
                sb.AppendLine("判定：" + verdictTag + " — " + r.Verdict);
                sb.AppendLine();
                sb.AppendLine("```");
                for (int j = 0; j < r.Observations.Count; j++) { sb.AppendLine(r.Observations[j]); }
                sb.AppendLine("```");
                sb.AppendLine();
            }
            sb.AppendLine("## 完整控制台输出");
            sb.AppendLine();
            sb.AppendLine("见同目录 `run-output.txt`。");
            sb.AppendLine();
            sb.AppendLine("<!-- HANDWRITTEN-SECTIONS-BELOW -->");
            sb.AppendLine();

            // 自动部分每次运行都重写，但手写分析章节必须保留 —— 否则重跑一次就把人工结论抹掉了。
            string target = Path.Combine(OutDir, "RESULTS.md");
            string tail = null;
            if (File.Exists(target))
            {
                try
                {
                    string old = File.ReadAllText(target, utf8);
                    const string marker = "<!-- HANDWRITTEN-SECTIONS-BELOW -->";
                    int at = old.IndexOf(marker, StringComparison.Ordinal);
                    if (at >= 0) { tail = old.Substring(at + marker.Length); }
                }
                catch (Exception) { }
            }
            if (tail != null && tail.Trim().Length > 0)
            {
                sb.Append(tail);
            }

            File.WriteAllText(target, sb.ToString(), utf8);
        }
    }
}
