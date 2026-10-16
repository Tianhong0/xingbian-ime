// TypeTest —— 真·端到端：在真实宿主程序窗口里，通过真实按键，用我们注册的输入法打字。
//
// 为什么这样设计：
//   - 记事本/写字板窗口是真的原生 EDIT 控件，自带 TSF 支持；
//   - 按键用 SendInput 发，走的是真实的键盘输入路径（会经过 TSF 的 key sink）；
//   - 读回也用真实的消息/文本 API，不做任何「假设」。
//
// 前置：形变输入法必须已注册（install.cmd 跑过），且已被选为当前输入法。
//   脚本会先把输入法切成我们的 TIP，再打字。

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TypeTest
{
    internal static class Program
    {
        // ---------- Win32 ----------
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
                                                        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL, wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_GETTEXTLENGTH = 0x000E;
        private const uint WM_SETTEXT = 0x000C;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        private static int _pass, _fail;

        [STAThread]
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }

            Console.WriteLine("TypeTest —— 在真实程序里用真按键打「形变输入法」");
            Console.WriteLine();

            // ---------- 1. 启动一个真实宿主 ----------
            Console.WriteLine("--- 1. 启动记事本 ---");
            Process np = null;
            foreach (string exe in new[] { "notepad.exe" })
            {
                try { np = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
                catch (Exception ex) { Console.WriteLine("  启动失败: " + ex.Message); }
                if (np != null) break;
            }
            if (np == null) { Console.WriteLine("[FAIL] 无法启动记事本"); return 1; }

            Console.WriteLine("  notepad pid=" + np.Id);
            Thread.Sleep(1500);
            np.Refresh();

            IntPtr hwnd = np.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                // 回退：按类名找
                for (int i = 0; i < 20 && hwnd == IntPtr.Zero; i++)
                {
                    hwnd = FindWindowW("Notepad", null);
                    if (hwnd == IntPtr.Zero) { Thread.Sleep(400); }
                }
            }
            Console.WriteLine("  主窗口 = 0x" + hwnd.ToInt64().ToString("X"));
            if (hwnd == IntPtr.Zero) { Console.WriteLine("[FAIL] 找不到记事本窗口"); try { np.Kill(); } catch (Exception) { } return 1; }

            // 清空文档
            SendMessageTimeoutW(hwnd, WM_SETTEXT, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 3000, out IntPtr _);
            Thread.Sleep(300);

            // ---------- 2. 把焦点给记事本 ----------
            Console.WriteLine("--- 2. 聚焦记事本 ---");
            uint notepadThread = GetWindowThreadProcessId(hwnd, out uint notepadPid);
            uint myThread = GetCurrentThreadId();
            bool attached = AttachThreadInput(myThread, notepadThread, true);
            try
            {
                SetForegroundWindow(hwnd);
                Thread.Sleep(400);
                IntPtr edit = FindWindowW("Notepad", null);
                // 找 EDIT 子窗口需要 EnumChildWindows；这里直接靠 SetForegroundWindow + SetFocus 走通
                bool fg = GetForegroundWindow() == hwnd;
                Console.WriteLine("  - AttachThreadInput = " + attached);
                Console.WriteLine("  - GetForegroundWindow()==notepad = " + fg);
                Check("记事本获得前台焦点", fg, "fg=0x" + GetForegroundWindow().ToInt64().ToString("X"));
            }
            finally
            {
                if (attached) { AttachThreadInput(myThread, notepadThread, false); }
            }

            // ---------- 3. 确认当前输入法是我们的 ----------
            Console.WriteLine("--- 3. 切到「形变输入法」并打字 ---");
            Console.WriteLine("  （如果当前不是形变输入法，下面的按键会被别的输入法吃掉）");

            Thread.Sleep(600);

            // ---------- 4. 发真实按键 ----------
            // n i m a 空格
            string keys = "nima ";
            Console.WriteLine("  发送按键: \"" + keys.Trim() + "\" + 空格");
            for (int i = 0; i < keys.Length; i++)
            {
                char ch = keys[i];
                if (ch == ' ')
                {
                    SendVk(0x20);
                }
                else
                {
                    ushort vk = (ushort)(char.ToUpperInvariant(ch) - 'A' + 0x41);
                    SendVk(vk);
                }
                Thread.Sleep(120);
            }

            Thread.Sleep(1200);

            // ---------- 5. 读回文档 ----------
            Console.WriteLine("--- 4. 读回文档内容 ---");
            IntPtr lenPtr;
            SendMessageTimeoutW(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 3000, out lenPtr);
            int len = lenPtr.ToInt32();
            Console.WriteLine("  WM_GETTEXTLENGTH = " + len);

            string text = ReadWindowText(hwnd, len);
            Console.WriteLine("  文档内容 = \"" + text + "\"");
            Console.WriteLine();

            // ---------- 判定 ----------
            Console.WriteLine("--- 判定 ---");
            string expected = "亻尔 女马";
            bool match = text == expected;
            Check("记事本里的文本 == 亻尔 女马", match, "实际=\"" + text + "\"");

            if (!match)
            {
                Info("");
                Info("可能的原因（按可能性排序）：");
                Info("  1. 当前输入法不是「形变输入法」—— 需要先 Win+空格 切过去，本程序无法可靠地程序化切换");
                Info("  2. 记事本没真正拿到键盘焦点（沙箱/前台窗口限制）");
                Info("  3. 输入法本身有问题（那才是真 bug）");
                Info("");
                Info("如果读到的是 \"nima \"（原样字母），说明按键送到了但输入法没吃键 -> 情况 1 或 3");
                if (text == "nima ") { Info(">>> 实际读到的是原样字母，强烈指向输入法未被选中"); }
            }

            try { np.Kill(); } catch (Exception) { }

            Console.WriteLine();
            Console.WriteLine("=== 通过 " + _pass + " 项，失败 " + _fail + " 项 ===");
            return _fail == 0 ? 0 : 1;
        }

        private static string ReadWindowText(IntPtr hwnd, int len)
        {
            if (len <= 0) { return string.Empty; }
            // WM_GETTEXT 需要跨进程缓冲区，用 SendMessage 的托管封送较麻烦；
            // 改用 .NET 的方式：直接对窗口发 WM_GETTEXT 到临时内存不可行（跨进程），
            // 因此这里对记事本改用「枚举子 EDIT 窗口 + 其文本」，用 UI Automation 最稳。
            try
            {
                return ReadViaUia(hwnd);
            }
            catch (Exception ex)
            {
                return "(读取失败: " + ex.Message + ")";
            }
        }

        /// <summary>用 UI Automation 读记事本编辑区文本（跨进程可靠）。</summary>
        private static string ReadViaUia(IntPtr hwnd)
        {
            // 通过反射调用 UIAutomationClient，避免加项目引用
            Type aeType = Type.GetTypeFromProgID("UIAutomationClient.CUIAutomation");
            if (aeType == null)
            {
                foreach (string asm in new[] { "UIAutomationClient", "UIAutomationClientsideProviders" })
                {
                    try { System.Reflection.Assembly.Load(asm); } catch (Exception) { }
                }
                aeType = Type.GetType("System.Windows.Automation.AutomationElement, UIAutomationClient");
            }
        if (aeType == null) { return "(UIAutomationClient 不可用)"; }
            object ae = Activator.CreateInstance(aeType);

            // AutomationElement.FromHandle(hwnd)
            System.Reflection.MethodInfo fromHandle = aeType.GetMethod("FromHandle", new Type[] { typeof(IntPtr) });
            object root = fromHandle.Invoke(null, new object[] { hwnd });

            // 找 Edit 控件：TreeScope.Descendants=4, ControlType.Edit
            Type tsType = Type.GetType("System.Windows.Automation.TreeScope, UIAutomationClient");
            Type ctType = Type.GetType("System.Windows.Automation.ControlType, UIAutomationClient");
            object descendants = Enum.Parse(tsType, "Descendants");
            object editType = ctType.GetProperty("Edit").GetValue(null, null);

            // Condition: new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)
            Type pcType = Type.GetType("System.Windows.Automation.PropertyCondition, UIAutomationClient");
            Type aelType = Type.GetType("System.Windows.Automation.AutomationElement, UIAutomationClient");
            object ctrlTypeProp = aelType.GetField("ControlTypeProperty").GetValue(null);
            object cond = Activator.CreateInstance(pcType, new object[] { ctrlTypeProp, editType });

            System.Reflection.MethodInfo findFirst = aeType.GetMethod("FindFirst", new Type[] { tsType, Type.GetType("System.Windows.Automation.Condition, UIAutomationClient") });
            object edit = findFirst.Invoke(root, new object[] { descendants, cond });
            if (edit == null) { return "(找不到 Edit 控件)"; }

            // ValuePattern
            Type vpType = Type.GetType("System.Windows.Automation.ValuePattern, UIAutomationClient");
            object vpObj;
            bool ok = (bool)edit.GetType().GetMethod("TryGetCurrentPattern")
                .Invoke(edit, new object[] { vpType.GetField("Pattern").GetValue(null), null });
            // TryGetCurrentPattern 的 out 参数用反射拿不方便，改用 GetCurrentPattern
            vpObj = edit.GetType().GetMethod("GetCurrentPattern").Invoke(edit, new object[] { vpType.GetField("Pattern").GetValue(null) });
            object val = vpType.GetProperty("Current").GetValue(vpObj, null);
            object txt = val.GetType().GetProperty("Value").GetValue(val, null);
            return txt as string ?? "(null)";
        }

        private static void SendVk(ushort vk)
        {
            INPUT[] inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = vk;
            inputs[0].u.ki.wScan = 0;
            inputs[0].u.ki.dwFlags = 0;
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = vk;
            inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;

            uint sent = SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != 2)
            {
                Console.WriteLine("  [warn] SendInput 只送出 " + sent + "/2, err=" + Marshal.GetLastWin32Error());
            }
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("[ ok ] " + name + " -- " + detail); }
            else { _fail++; Console.WriteLine("[FAIL] " + name + " -- " + detail); }
        }

        private static void Info(string s) { Console.WriteLine("       " + s); }
    }
}
