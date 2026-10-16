// RealHost v3 —— 真实记事本 + 真实按键 + 真实 TSF 档案切换 + 两个独立证据源
//
// 演进（每一版都修掉了上一版暴露的方法论问题，v3 保留全部自我校正）：
//
// v1 错误：ActivateProfile 的 dwProfileType 传 0（应为 1）；读回无对照实验；
//          把 GetActiveProfile 当"记事本当前输入法"（TSF 档案是按线程的）。
// v2 错误：把 notepad 的“文档是空的”当结论 —— 但 v2 自己打印的
//          `前台窗口 == 记事本 : False` 已经说明 SendInput 根本没送到记事本。
//          SendInput 只发给**前台线程的焦点窗口**，前台没抢到，一切都白谈。
//          WM_GETTEXT 也不行：v2 往 notepad 里 VirtualAllocEx 再传远程指针，
//          WM_GETTEXT 是 < WM_USER 的系统消息，USER32 本来就会替我们跨进程编组，
//          再叠一个远程指针反而让调用失败（实测 SendMessageTimeout fail、读到 0 字符）。
//
// v3 的三处修正：
//   1. 抢前台：AttachThreadInput 到当前前台线程后再 SetForegroundWindow，
//      抢完立刻 detach。抢不到就**明确报告并把结论降级**，不再把"空"当结论。
//   2. WM_GETTEXT 用**本进程自己的缓冲区**，让 USER32 去编组。
//   3. 增加一个**不依赖读回文本**的独立证据源：候选浮窗是一个顶层窗口，
//      窗口类名是 `XingbianIME.CandidatePanel.Window`（CandidatePanel.cs:41）。
//      只要它出现在记事本进程里，就证明托管 TextService 真的在 notepad.exe 内
//      被加载并执行了（这条证据不受"文本没读出来"影响）。
//
// 诚实边界：即便三样都成立，也不能替代"人坐在电脑前用真键盘敲"的验证。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RealHost
{
    [ComImport, Guid("71C6E74C-0F28-11D8-A82A-00065B84435C"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITfInputProcessorProfileMgr
    {
        [PreserveSig]
        int ActivateProfile(uint dwProfileType, ushort langid, ref Guid clsid,
                            ref Guid guidProfile, IntPtr hkl, uint dwFlags);
        [PreserveSig]
        int DeactivateProfile(uint dwProfileType, ushort langid, ref Guid clsid,
                              ref Guid guidProfile, IntPtr hkl, uint dwFlags);
        [PreserveSig]
        int GetProfile(uint dwProfileType, ushort langid, ref Guid clsid,
                       ref Guid guidProfile, IntPtr pProfile);
        [PreserveSig]
        int EnumProfiles(ushort langid, out IntPtr ppEnum);
        [PreserveSig]
        int ReleaseInputProcessor(ref Guid clsid, uint dwFlags);
        [PreserveSig]
        int RegisterProfile(ref Guid clsid, ushort langid, ref Guid guidProfile,
                            IntPtr pchDesc, uint cchDesc, IntPtr pchIconFile,
                            uint cchIconFile, uint uIconIndex, IntPtr hklSubstitute,
                            uint dwPreferredLayout, uint dwFlags);
        [PreserveSig]
        int UnregisterProfile(ref Guid clsid, ushort langid, ref Guid guidProfile, uint dwFlags);
        [PreserveSig]
        int GetActiveProfile(ref Guid catid, IntPtr pProfile);
    }

    internal static class Program
    {
        private const string PanelClass = "XingbianIME.CandidatePanel.Window";

        private static readonly Guid CLSID_InputProcessorProfiles =
            new Guid("33c53a50-f456-4884-b049-85fd643ecfed");
        private static readonly Guid IID_ITfInputProcessorProfileMgr =
            new Guid("71C6E74C-0F28-11D8-A82A-00065B84435C");

        private static readonly Guid Clsid = new Guid("7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43");
        private static readonly Guid Profile = new Guid("B3F2C8D1-6A47-4C09-8E52-1D7A9F0B4C65");
        private static readonly Guid MsPinyinClsid = new Guid("81d4e9c9-1d3b-41bc-9e6c-4b40bf79e35e");
        private static readonly Guid MsPinyinProfile = new Guid("FA550B04-5AD7-411f-A5AC-CA038EC515D7");
        private const ushort LangId = 0x0804;

        private static readonly Guid GUID_TFCAT_TIP_KEYBOARD =
            new Guid("34745C63-B2F0-4784-8B67-5E12C8701A31");

        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private const uint WM_SETTEXT = 0x000C;
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_GETTEXTLENGTH = 0x000E;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_CHAR = 0x0102;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint GW_OWNER = 4;

        private const uint TF_PROFILETYPE_INPUTPROCESSOR = 1;
        private const uint TF_IPPMF_FORPROCESS = 0x10000000;
        private const uint TF_IPPMF_FORSESSION = 0x20000000;

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr pv, uint dwCoInit);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter,
                                                   uint dwClsContext, ref Guid riid,
                                                   out IntPtr ppv);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string cls, string name);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr child, string cls, string name);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w,
                                                         IntPtr l, uint flags, uint ms,
                                                         out IntPtr result);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumProc cb, IntPtr p);
        private delegate bool EnumProc(IntPtr h, IntPtr p);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf,
                                                       int len, out int retLen);
        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion u; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo;
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint n, INPUT[] p, int cb);
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static int _pass, _fail;

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("[ ok ] " + name + (detail.Length > 0 ? "  -- " + detail : "")); }
            else { _fail++; Console.WriteLine("[FAIL] " + name + (detail.Length > 0 ? "  -- " + detail : "")); }
        }

        private static string IntegrityOf(IntPtr hProcess)
        {
            IntPtr tok;
            if (!OpenProcessToken(hProcess, 0x0008, out tok))
            {
                return "(OpenProcessToken err=" + Marshal.GetLastWin32Error() + ")";
            }
            try
            {
                int need;
                GetTokenInformation(tok, 25, IntPtr.Zero, 0, out need);
                IntPtr buf = Marshal.AllocHGlobal(need);
                try
                {
                    if (!GetTokenInformation(tok, 25, buf, need, out need))
                    {
                        return "(GetTokenInformation err=" + Marshal.GetLastWin32Error() + ")";
                    }
                    IntPtr sid = Marshal.ReadIntPtr(buf);
                    IntPtr cnt = GetSidSubAuthorityCount(sid);
                    int n = Marshal.ReadByte(cnt);
                    IntPtr rid = GetSidSubAuthority(sid, n - 1);
                    uint v = (uint)Marshal.ReadInt32(rid);
                    string nm = v >= 0x4000 ? "System" : v >= 0x3000 ? "High"
                              : v >= 0x2000 ? "Medium" : v >= 0x1000 ? "Low" : "Untrusted";
                    return nm + " (0x" + v.ToString("X4") + ")";
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(tok); }
        }

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }
            Console.WriteLine("RealHost v3 —— 真实记事本 + 真实按键 + 两个独立证据源");
            Console.WriteLine();

            CoInitializeEx(IntPtr.Zero, 2);

            if (args.Length > 0 && args[0] == "--latency") { return LatencyMode(); }

            Console.WriteLine("--- 0. 环境 ---");
            Console.WriteLine("  本进程完整性 = " + IntegrityOf(GetCurrentProcess()));
            Console.WriteLine("  本线程活动档案 = " + DescribeActive());
            IntPtr fg0 = GetForegroundWindow();
            uint fg0Pid;
            GetWindowThreadProcessId(fg0, out fg0Pid);
            Console.WriteLine("  当前前台窗口 = 0x" + fg0.ToInt64().ToString("X")
                              + "  pid=" + fg0Pid);
            Console.WriteLine();

            Console.WriteLine("--- 1. 把形变输入法设为活动档案 ---");
            Console.WriteLine("  dwFlags=0            hr=0x" + TryActivate(0).ToString("X8"));
            Console.WriteLine("  TF_IPPMF_FORPROCESS  hr=0x" + TryActivate(TF_IPPMF_FORPROCESS).ToString("X8"));
            Console.WriteLine("  TF_IPPMF_FORSESSION  hr=0x" + TryActivate(TF_IPPMF_FORSESSION).ToString("X8"));
            Console.WriteLine("  （FORSESSION 是给之后新起的进程/线程用的，必须在启动记事本之前设）");
            Console.WriteLine("  本线程活动档案 = " + DescribeActive());
            Console.WriteLine();

            Console.WriteLine("--- 2. 启动记事本 ---");
            Process np = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            if (np == null) { Console.WriteLine("[FAIL] 无法启动记事本"); CoUninitialize(); return 1; }

            IntPtr hwnd = IntPtr.Zero, edit = IntPtr.Zero;
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(200);
                if (hwnd == IntPtr.Zero)
                {
                    hwnd = FindWindowW("Notepad", null);
                    if (hwnd == IntPtr.Zero) { try { np.Refresh(); hwnd = np.MainWindowHandle; } catch (Exception) { } }
                }
                if (hwnd != IntPtr.Zero && edit == IntPtr.Zero)
                {
                    edit = FindWindowExW(hwnd, IntPtr.Zero, "Edit", null);
                }
                if (hwnd != IntPtr.Zero && edit != IntPtr.Zero) { break; }
            }
            IntPtr npProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)np.Id);
            Console.WriteLine("  pid=" + np.Id + "  完整性=" + IntegrityOf(npProc)
                              + "  hwnd=0x" + hwnd.ToInt64().ToString("X")
                              + "  EDIT=0x" + edit.ToInt64().ToString("X"));
            if (npProc != IntPtr.Zero) { CloseHandle(npProc); }
            Check("找到记事本主窗口与 EDIT 控件", hwnd != IntPtr.Zero && edit != IntPtr.Zero, "");
            if (hwnd == IntPtr.Zero || edit == IntPtr.Zero)
            {
                try { np.Kill(); } catch (Exception) { }
                CoUninitialize(); return 1;
            }
            Console.WriteLine();

            // ---------- 3. 抢前台（v2 就是栽在这里）----------
            Console.WriteLine("--- 3. 抢前台（SendInput 只发给前台线程的焦点窗口）---");
            bool fgOk = ForceForeground(hwnd);
            Console.WriteLine("  抢完后 前台窗口 == 记事本 : " + (GetForegroundWindow() == hwnd));
            Console.WriteLine("  抢完后 前台窗口 = 0x" + GetForegroundWindow().ToInt64().ToString("X"));
            Check("记事本拿到前台焦点", fgOk, fgOk ? "" : "抢不到前台 -> SendInput 结果不可采信");
            if (!fgOk)
            {
                Console.WriteLine();
                Console.WriteLine("  抢不到前台说明本会话不允许程序化切换前台窗口（前台锁定）。");
                Console.WriteLine("  这时 SendInput 打到的是别处，不能用\"文档是空的\"推断输入法有问题。");
                Console.WriteLine("  仍继续跑，但结论按\"按键路径未验证\"降级处理。");
            }
            Console.WriteLine();

            // ---------- 4. 对照实验 ----------
            Console.WriteLine("--- 4. 对照实验：WM_SETTEXT 写已知串再读回（读回用本进程缓冲区）---");
            bool wrote = SetText(edit, "对照ABC");
            string back = ReadEditText(edit, "对照");
            bool readOk = (back == "对照ABC");
            Console.WriteLine("  WM_SETTEXT 返回 " + wrote + "，读回 = \"" + back + "\"");
            Check("读回通路可用", readOk, readOk ? "" : "读回=\"" + back + "\"");
            Console.WriteLine();

            // ---------- 5. 打字 ----------
            SetText(edit, "");
            Thread.Sleep(300);
            ForceForeground(hwnd);
            Thread.Sleep(400);

            Console.WriteLine("--- 5. 发送按键 n i m a 空格 ---");
            IntPtr panelBefore = FindWindowW(PanelClass, null);
            Console.WriteLine("  打字前 候选浮窗(" + PanelClass + ") = 0x"
                              + panelBefore.ToInt64().ToString("X")
                              + "  可见=" + (panelBefore != IntPtr.Zero && IsWindowVisible(panelBefore)));
            var seen = new List<string>();
            SendVk(0x4E); Thread.Sleep(150);
            seen.Add(SnapPanel("按 N 后"));
            SendVk(0x49); Thread.Sleep(150);
            seen.Add(SnapPanel("按 I 后"));
            SendVk(0x4D); Thread.Sleep(150);
            seen.Add(SnapPanel("按 M 后"));
            SendVk(0x41); Thread.Sleep(150);
            seen.Add(SnapPanel("按 A 后"));
            Thread.Sleep(500);
            string text = ReadEditText(edit, "打字");
            Console.WriteLine("  文本 = \"" + text + "\"  长度 = " + (text == null ? -1 : text.Length) + " 码元");
            if (!string.IsNullOrEmpty(text))
            {
                StringBuilder cps = new StringBuilder();
                foreach (char ch in text) { cps.Append("U+").Append(((int)ch).ToString("X4")).Append(' '); }
                Console.WriteLine("  码点 = " + cps.ToString().Trim());
            }
            Console.WriteLine("  --- 面板快照序列 ---");
            foreach (string s in seen) { Console.WriteLine("    " + s); }

            Console.WriteLine();
            SendVk(0x20); Thread.Sleep(900);
            string final = ReadEditText(edit, "空格后");
            Console.WriteLine("  空格后文本 = \"" + final + "\"  长度 = " + (final == null ? -1 : final.Length) + " 码元");
            if (!string.IsNullOrEmpty(final))
            {
                StringBuilder cps = new StringBuilder();
                foreach (char ch in final) { cps.Append("U+").Append(((int)ch).ToString("X4")).Append(' '); }
                Console.WriteLine("  码点 = " + cps.ToString().Trim());
            }
            Console.WriteLine();

            // ---------- 6. 面板窗口归属 ----------
            Console.WriteLine("--- 6. 候选浮窗（不依赖读回文本的独立证据）---");
            IntPtr panel = FindWindowW(PanelClass, null);
            uint panelPid = 0;
            if (panel != IntPtr.Zero) { GetWindowThreadProcessId(panel, out panelPid); }
            Console.WriteLine("  候选浮窗句柄 = 0x" + panel.ToInt64().ToString("X"));
            Console.WriteLine("  候选浮窗进程 = " + panelPid + "   （记事本 = " + np.Id + "）");
            Console.WriteLine("  候选浮窗可见 = " + (panel != IntPtr.Zero && IsWindowVisible(panel)));
            bool panelInNotepad = (panel != IntPtr.Zero && panelPid == (uint)np.Id);
            Check("候选浮窗出现在记事本进程内（= 托管 TextService 真的在 notepad 里跑起来了）",
                  panelInNotepad,
                  panelInNotepad ? "" : "hwnd=0x" + panel.ToInt64().ToString("X") + " pid=" + panelPid);
            Console.WriteLine();

            // ---------- 7. 判定 ----------
            Console.WriteLine("--- 7. 判定 ---");
            bool textOk = (final == "亻尔 女马");
            Check("记事本内容 == \"亻尔 女马\"", textOk, "实际=\"" + final + "\"");
            Console.WriteLine();
            Console.WriteLine("  解读：");
            if (textOk)
            {
                Console.WriteLine("   全链打通：真记事本里真按键 nima+空格 得到 亻尔 女马。");
            }
            else if (panelInNotepad)
            {
                Console.WriteLine("   记事本里确实加载了我们的输入法（候选浮窗在 notepad 进程内），");
                Console.WriteLine("   但文本没读出来。可能是：候选浮窗在打字时未真正 Show（文本为空时");
                Console.WriteLine("   面板会隐藏）、或抢前台失败导致按键没送达。");
            }
            else if (!fgOk)
            {
                Console.WriteLine("   抢前台失败 -> 按键没送到记事本 -> 本条测试**未验证**，");
                Console.WriteLine("   不能据此认为输入法有问题（需要真人用真键盘在沙箱外验证）。");
            }
            else if (string.IsNullOrEmpty(final))
            {
                Console.WriteLine("   前台已抢到、按键已送出，但文档为空；且 notepad 里没有候选浮窗");
                Console.WriteLine("   -> 我们的 TIP 很可能没被激活到记事本线程上（档案未生效）。");
            }
            else if (final.Trim() == "nima")
            {
                Console.WriteLine("   原样字母 -> 按键送到了，但我们的 TIP 没吃键（档案未生效到该线程）。");
            }
            else
            {
                Console.WriteLine("   其它内容 -> 被别的输入法处理了。");
            }

            // ---------- 8. 收尾 ----------
            Console.WriteLine();
            Console.WriteLine("--- 8. 收尾：本线程活动档案还原成微软拼音 ---");
            Guid c2 = MsPinyinClsid, g2 = MsPinyinProfile;
            IntPtr pm2 = IntPtr.Zero;
            Guid cp = CLSID_InputProcessorProfiles, im = IID_ITfInputProcessorProfileMgr;
            int hr2 = CoCreateInstance(ref cp, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref im, out pm2);
            if (hr2 == 0 && pm2 != IntPtr.Zero)
            {
                ITfInputProcessorProfileMgr mgr =
                    (ITfInputProcessorProfileMgr)Marshal.GetObjectForIUnknown(pm2);
                int hr3 = mgr.ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, LangId,
                                              ref c2, ref g2, IntPtr.Zero, 0);
                Console.WriteLine("  还原 hr=0x" + hr3.ToString("X8"));
            }
            try { np.Kill(); } catch (Exception) { }
            CoUninitialize();
            Console.WriteLine();
            Console.WriteLine("=== 通过 " + _pass + " 项，失败 " + _fail + " 项 ===");
            return _fail == 0 ? 0 : 1;
        }

        /// <summary>
        /// 首键延迟实测 + TIP 内部自检。
        /// 动机：`TextService.Warmup` 在源码里**没有任何调用点**（全项目 grep 只有定义本身），
        /// 所以词典是懒加载的 —— 宿主进程里第一次敲字母时才付 ~240ms 的代价，而且是
        /// 在宿主的 UI 线程上阻塞。这里把它量出来，不靠推测。
        /// </summary>
        private static int LatencyMode()
        {
            const string tipPath = @"C:\Program Files\XingbianIME\Xingbian.Tsf.dll";
            Console.WriteLine("--- 首键延迟 + TIP 自检 ---");
            Console.WriteLine();

            System.Reflection.Assembly asm = null;
            try { asm = System.Reflection.Assembly.LoadFrom(tipPath); }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] 加载 " + tipPath + " 失败：" + ex.GetType().Name + ": " + ex.Message);
                CoUninitialize();
                return 1;
            }
            Console.WriteLine("已加载 " + asm.Location);
            Console.WriteLine("  Xingbian.Core 版本 = " + asm.GetReferencedAssemblies().Length + " 个引用");
            Type tsType = asm.GetType("Xingbian.Tsf.TextService", false);
            if (tsType == null) { Console.WriteLine("[FAIL] 找不到 Xingbian.Tsf.TextService"); CoUninitialize(); return 1; }
            Console.WriteLine("  类型 = " + tsType.FullName + "  (" + tsType.AssemblyQualifiedName.Length + " 字节 AQN)");

            // --- 1. 反射调静态 Warmup，量加载耗时 ---
            System.Reflection.MethodInfo warm = tsType.GetMethod("Warmup",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (warm == null) { Console.WriteLine("[FAIL] 找不到 Warmup"); CoUninitialize(); return 1; }

            var sw = Stopwatch.StartNew();
            object[] wargs = new object[] { null };
            bool ok = (bool)warm.Invoke(null, wargs);
            sw.Stop();
            Console.WriteLine("  Warmup() -> " + ok + "  耗时 = " + sw.ElapsedMilliseconds + " ms"
                              + "  error=" + (wargs[0] == null ? "(null)" : wargs[0].ToString()));
            long firstLoadMs = sw.ElapsedMilliseconds;
            Check("词典能在安装态加载成功", ok, "耗时 " + firstLoadMs + " ms");

            // --- 2. EngineError 属性 ---
            System.Reflection.PropertyInfo ee = tsType.GetProperty("EngineError",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (ee != null)
            {
                object v = ee.GetValue(null, null);
                Console.WriteLine("  EngineError = " + (v == null ? "(null)" : v.ToString()));
                Check("EngineError 为空（= 没有加载错误）", v == null, "");
            }

            // --- 3. 再调一次，验证缓存生效（第二次应接近 0ms）---
            sw.Restart();
            wargs = new object[] { null };
            warm.Invoke(null, wargs);
            sw.Stop();
            Console.WriteLine("  第二次 Warmup() 耗时 = " + sw.ElapsedMilliseconds + " ms (应接近 0，证明进程级缓存生效)");
            Check("进程级缓存生效（第二次不重新加载）", sw.ElapsedMilliseconds < 50,
                  sw.ElapsedMilliseconds + " ms");

            // --- 4. 结论：第一次按键要付多少代价 ---
            Console.WriteLine();
            Console.WriteLine("  结论：宿主进程里**第一次敲字母**要付 " + firstLoadMs
                              + " ms 的阻塞代价（发生在宿主 UI 线程上）。");
            Console.WriteLine("  这就是候选浮窗在\"按 N 后还不存在、按 I 后才出现\"的原因。");
            Console.WriteLine("  Warmup 是 public static 且设计上就是给自检/预热用的，但全项目没有任何调用点。");

            CoUninitialize();
            Console.WriteLine();
            Console.WriteLine("=== 通过 " + _pass + " 项，失败 " + _fail + " 项 ===");
            return _fail == 0 ? 0 : 1;
        }

        /// <summary>抢前台：AttachThreadInput 到当前前台线程后再抢，抢完立刻 detach。</summary>
        private static bool ForceForeground(IntPtr hwnd)
        {
            ShowWindow(hwnd, 9 /*SW_RESTORE*/);
            Thread.Sleep(200);
            if (GetForegroundWindow() == hwnd) { return true; }

            uint myTid = GetCurrentThreadId();
            IntPtr fg = GetForegroundWindow();
            uint fgTid = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;

            bool attached = false;
            if (fgTid != 0 && fgTid != myTid)
            {
                attached = AttachThreadInput(myTid, fgTid, true);
                Console.WriteLine("  AttachThreadInput(mine=" + myTid + ", fg=" + fgTid + ") = " + attached);
            }
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                Thread.Sleep(400);
                if (GetForegroundWindow() == hwnd) { return true; }
                // 再试一次：先最小化再还原，有时能打断前台锁
                ShowWindow(hwnd, 6 /*SW_MINIMIZE*/);
                Thread.Sleep(250);
                ShowWindow(hwnd, 9 /*SW_RESTORE*/);
                Thread.Sleep(250);
                SetForegroundWindow(hwnd);
                Thread.Sleep(400);
                return GetForegroundWindow() == hwnd;
            }
            finally
            {
                if (attached) { AttachThreadInput(myTid, fgTid, false); }
            }
        }

        private static string SnapPanel(string tag)
        {
            IntPtr p = FindWindowW(PanelClass, null);
            if (p == IntPtr.Zero) { return tag + " : 候选浮窗不存在"; }
            uint pid;
            GetWindowThreadProcessId(p, out pid);
            return tag + " : 候选浮窗 hwnd=0x" + p.ToInt64().ToString("X")
                   + " pid=" + pid + " 可见=" + IsWindowVisible(p);
        }

        private static bool SetText(IntPtr edit, string s)
        {
            IntPtr lp = Marshal.StringToHGlobalUni(s);
            try
            {
                IntPtr res;
                IntPtr ok = SendMessageTimeoutW(edit, WM_SETTEXT, IntPtr.Zero, lp,
                                                SMTO_ABORTIFHUNG, 3000, out res);
                return ok != IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(lp); }
        }

        private static void SendVk(ushort vk)
        {
            INPUT[] ins = new INPUT[2];
            ins[0].type = INPUT_KEYBOARD; ins[0].u.ki.wVk = vk;
            ins[1].type = INPUT_KEYBOARD; ins[1].u.ki.wVk = vk;
            ins[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
            uint sent = SendInput(2, ins, Marshal.SizeOf(typeof(INPUT)));
            if (sent != 2) { Console.WriteLine("  [warn] SendInput vk=0x" + vk.ToString("X2")
                + " 只送出 " + sent + " 个事件 err=" + Marshal.GetLastWin32Error()); }
        }

        private static int TryActivate(uint flags)
        {
            IntPtr pm = IntPtr.Zero;
            Guid cp = CLSID_InputProcessorProfiles, im = IID_ITfInputProcessorProfileMgr;
            int hr = CoCreateInstance(ref cp, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref im, out pm);
            if (hr != 0 || pm == IntPtr.Zero) { return hr; }
            ITfInputProcessorProfileMgr mgr =
                (ITfInputProcessorProfileMgr)Marshal.GetObjectForIUnknown(pm);
            Guid c = Clsid, g = Profile;
            return mgr.ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, LangId, ref c, ref g,
                                       IntPtr.Zero, flags);
        }

        private static string DescribeActive()
        {
            IntPtr pm = IntPtr.Zero;
            Guid cp = CLSID_InputProcessorProfiles, im = IID_ITfInputProcessorProfileMgr;
            int hr = CoCreateInstance(ref cp, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref im, out pm);
            if (hr != 0 || pm == IntPtr.Zero) { return "(CoCreateInstance hr=0x" + hr.ToString("X8") + ")"; }
            ITfInputProcessorProfileMgr mgr =
                (ITfInputProcessorProfileMgr)Marshal.GetObjectForIUnknown(pm);
            IntPtr buf = Marshal.AllocCoTaskMem(160);
            try
            {
                for (int i = 0; i < 160; i++) { Marshal.WriteByte(buf, i, 0); }
                Guid cat = GUID_TFCAT_TIP_KEYBOARD;
                int hrG = mgr.GetActiveProfile(ref cat, buf);
                if (hrG != 0) { return "(GetActiveProfile hr=0x" + hrG.ToString("X8") + ")"; }
                uint type = (uint)Marshal.ReadInt32(buf, 0);
                ushort lang = (ushort)Marshal.ReadInt16(buf, 4);
                byte[] b = new byte[16];
                Marshal.Copy(buf + 8, b, 0, 16);
                Guid clsid = new Guid(b);
                Marshal.Copy(buf + 24, b, 0, 16);
                Guid pr = new Guid(b);
                string who = clsid.Equals(Clsid) ? "形变输入法"
                           : clsid.Equals(MsPinyinClsid) ? "微软拼音" : "未知";
                return who + "  type=" + type + " lang=0x" + lang.ToString("X4");
            }
            finally { Marshal.FreeCoTaskMem(buf); }
        }

        /// <summary>
        /// 读 EDIT 文本。关键：缓冲区在**本进程**，WM_GETTEXT(&lt; WM_USER) 由 USER32 负责跨进程编组。
        /// （v2 往目标进程 VirtualAllocEx 传远程指针，反而让 USER32 的编组失效 -> 读到 0 字符。）
        /// </summary>
        private static string ReadEditText(IntPtr edit, string tag)
        {
            IntPtr lenPtr;
            IntPtr okLen = SendMessageTimeoutW(edit, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero,
                                               SMTO_ABORTIFHUNG, 3000, out lenPtr);
            int len = lenPtr.ToInt32();
            Console.WriteLine("     [" + tag + "] WM_GETTEXTLENGTH = " + len
                              + "  (超时? " + (okLen == IntPtr.Zero) + ")");
            if (len <= 0) { return string.Empty; }

            int bytes = (len + 2) * 2;
            IntPtr local = Marshal.AllocHGlobal(bytes);
            try
            {
                for (int i = 0; i < bytes; i++) { Marshal.WriteByte(local, i, 0); }
                IntPtr got;
                IntPtr okGet = SendMessageTimeoutW(edit, WM_GETTEXT, (IntPtr)(len + 1), local,
                                                   SMTO_ABORTIFHUNG, 3000, out got);
                int n = got.ToInt32();
                Console.WriteLine("     [" + tag + "] WM_GETTEXT -> " + n + " 字符  (超时? "
                                  + (okGet == IntPtr.Zero) + ")");
                if (n <= 0) { return string.Empty; }
                if (n > len + 1) { n = len; }
                return Marshal.PtrToStringUni(local, n);
            }
            finally { Marshal.FreeHGlobal(local); }
        }
    }
}
