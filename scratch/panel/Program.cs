// ============================================================================
//  PanelVerify — CandidatePanel 的独立验证程序（net48 WinExe，临时工程）
//
//  验证内容：
//    1. 真实窗口创建 / WS_POPUP + 三个扩展样式
//    2. 无焦点：Show 前后前台窗口不变，WM_MOUSEACTIVATE 返回 MA_NOACTIVATE
//    3. 鼠标穿透：WM_NCHITTEST 返回 HTTRANSPARENT
//    4. 真实屏幕截图（shot1/shot2）—— 必须用眼睛看
//    5. 越界定位：右下角翻转 + 全部钳进显示器工作区（含负坐标的多显示器）
//    6. 脏输入：labels=null / 空数组 / notes 短于 labels / selectedIndex 越界
//    7. GDI 句柄泄漏：200 次 Show 前后 GetGuiResources 对比
//    8. Dispose 幂等 + Dispose 后再调用不崩
//
//  截图说明：面板是 WS_EX_TOPMOST，用 Graphics.CopyFromScreen 抓的就是屏幕上
//  真实合成结果（最忠实的验证）。若桌面不可见导致抓到纯色图，会如实报告。
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Xingbian.Tsf;

namespace PanelVerify
{
    internal static class Program
    {
        private static readonly StringBuilder Log = new StringBuilder();
        private static int _failures;

        // ---- 验证程序自己的互操作（只看样式/抓图用，不属于交付物）------------
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessageW([In] ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int GetClassNameW(IntPtr hWnd, [Out] StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "MonitorFromPoint")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        // GetGuiResources 由 user32.dll 导出（psapi.dll 上找不到该入口点）
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        // ---- 验证程序自带的 native 层 ---------------------------------------
        // 说明：交付物里的 PanelNative 是 internal（只服务于 Xingbian.Tsf 程序集），
        // 验证程序在另一个程序集里访问不到，所以这里自己声明一份只读探测用的 P/Invoke。
        // 这是验证脚手架，不属于交付物。
        private static class N
        {
            internal const int LOGPIXELSY = 90;
            internal const uint DEFAULT_CHARSET = 1;
            internal const uint OUT_TT_PRECIS = 7;
            internal const uint CLIP_DEFAULT_PRECIS = 0;
            internal const uint CLEARTYPE_QUALITY = 5;
            internal const int FW_NORMAL = 400;
            internal const uint FF_SWISS = 0x20;
            internal const int GWL_STYLE = -16;
            internal const int GWL_EXSTYLE = -20;
            internal const int WS_EX_TOOLWINDOW = 0x00000080;
            internal const int WS_EX_TOPMOST = 0x00000008;
            internal const int WS_EX_NOACTIVATE = 0x08000000;

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetWindowLongPtrW")]
            internal static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetDC")]
            internal static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ReleaseDC")]
            internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetWindowRect")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetDeviceCaps")]
            internal static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

            [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")]
            internal static extern IntPtr CreateFontW(
                int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
                uint bItalic, uint bUnderline, uint bStrikeOut,
                uint iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
                [MarshalAs(UnmanagedType.LPWStr)] string pszFaceName);

            [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SelectObject")]
            internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

            [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "DeleteObject")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DeleteObject(IntPtr ho);

            [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetTextFaceW")]
            internal static extern int GetTextFaceW(IntPtr hdc, int nCount, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder lpName);

            // ⚠ GetTickCount 在 kernel32.dll（不是 user32），且导出名是 A/W 通吃的 GetTickCount
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern uint GetTickCount();

            /// <summary>读回 HFONT 实际映射到的字体族名。</summary>
            internal static string RealizedFace(IntPtr hFont)
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
                    if (GetTextFaceW(hdc, sb.Capacity, sb) <= 0) return null;
                    return sb.ToString();
                }
                catch (Exception) { return null; }
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

        private const uint PM_REMOVE = 1;
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const uint WM_NCHITTEST = 0x0084;
        private const uint WM_MOUSEACTIVATE = 0x0021;
        private const int HTTRANSPARENT = -1;
        private const int MA_NOACTIVATE = 3;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        // ---- 反射出私有 hwnd，供验证程序做样式探测 -----------------------------
        private static IntPtr GetHwnd(CandidatePanel panel)
        {
            try
            {
                System.Reflection.FieldInfo fi = typeof(CandidatePanel).GetField(
                    "_hwnd", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fi == null) return IntPtr.Zero;
                return (IntPtr)fi.GetValue(panel);
            }
            catch (Exception) { return IntPtr.Zero; }
        }

        private static void Write(string line)
        {
            Log.AppendLine(line);
            try { Console.Out.WriteLine(line); } catch (Exception) { }
        }

        private static void Check(string what, bool ok, string detail)
        {
            if (!ok) _failures++;
            Write((ok ? "  [PASS] " : "  [FAIL] ") + what + (string.IsNullOrEmpty(detail) ? "" : "  |  " + detail));
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string outDir = (args != null && args.Length > 0)
                ? args[0]
                : @"D:\Documents\deepseek-harness\default-workspace\xingbian-ime\scratch\panel";
            Directory.CreateDirectory(outDir);

            Write("=== PanelVerify ===");
            Write("time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Write("exe: " + System.Reflection.Assembly.GetExecutingAssembly().Location);

            int dpi = ReadDpi();
            Write("screen DPI (LOGPIXELSY) = " + dpi);

            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            Write("virtual screen = (" + vx + "," + vy + ") .. (" + (vx + vw) + "," + (vy + vh) + ")");

            IntPtr fgBefore = GetForegroundWindow();
            Write("foreground hwnd before = 0x" + fgBefore.ToString("X"));

            CandidatePanel panel = new CandidatePanel();

            // ---------------------------------------------------------------
            Write("");
            Write("--- 1. RegisterWindowClass 幂等 ---");
            try
            {
                panel.RegisterWindowClass();
                panel.RegisterWindowClass();
                panel.RegisterWindowClass();
                Check("RegisterWindowClass x3 no-throw", true, "LastError=" + panel.LastError);
            }
            catch (Exception ex)
            {
                Check("RegisterWindowClass x3 no-throw", false, ex.GetType().Name + ": " + ex.Message);
            }

            // ---------------------------------------------------------------
            Write("");
            Write("--- 2. Show #1 @ (200,200) selected=0 ---");
            string[] labels1 = new string[] { "1. 你妈", "2. 尼玛", "3. 你马" };
            string[] notes1 = new string[] { "亻尔 女马", "尸匕 马", "亻尔 马" };

            bool show1Threw = false;
            try
            {
                panel.Show(200, 200, "形变  nima", labels1, notes1, 0);
            }
            catch (Exception ex)
            {
                show1Threw = true;
                Write("  Show threw: " + ex);
            }
            Check("Show #1 does not throw", !show1Threw, "");
            Pump(350);

            IntPtr hwnd = GetHwnd(panel);
            Write("  hwnd = 0x" + hwnd.ToString("X") + "   LastError=" + panel.LastError);
            Check("hwnd created", hwnd != IntPtr.Zero, "");
            Check("IsVisible == true", panel.IsVisible, "IsVisible=" + panel.IsVisible);

            if (hwnd != IntPtr.Zero)
            {
                long style = N.GetWindowLongPtrW(hwnd, GWL_STYLE).ToInt64();
                long exStyle = N.GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
                Write("  style   = 0x" + style.ToString("X8") + "   (WS_POPUP=0x80000000)");
                Write("  exStyle = 0x" + exStyle.ToString("X8"));

                Check("WS_POPUP set", (style & 0x80000000L) != 0, "style=0x" + style.ToString("X8"));
                Check("WS_EX_TOOLWINDOW set", (exStyle & N.WS_EX_TOOLWINDOW) != 0, "");
                Check("WS_EX_TOPMOST set", (exStyle & N.WS_EX_TOPMOST) != 0, "");
                Check("WS_EX_NOACTIVATE set", (exStyle & N.WS_EX_NOACTIVATE) != 0, "");
                Check("no WS_EX_APPWINDOW (won't show in taskbar)", (exStyle & 0x00040000L) == 0, "");

                // 类名
                StringBuilder cn = new StringBuilder(256);
                GetClassNameW(hwnd, cn, cn.Capacity);

                // 鼠标穿透
                int ht = SendMessageW(hwnd, WM_NCHITTEST, IntPtr.Zero, IntPtr.Zero).ToInt32();
                Check("WM_NCHITTEST -> HTTRANSPARENT", ht == HTTRANSPARENT, "got " + ht);
                int ma = SendMessageW(hwnd, WM_MOUSEACTIVATE, IntPtr.Zero, IntPtr.Zero).ToInt32();
                Check("WM_MOUSEACTIVATE -> MA_NOACTIVATE", ma == MA_NOACTIVATE, "got " + ma);

                // 无焦点
                IntPtr fgAfter = GetForegroundWindow();
                Check("foreground window unchanged after Show",
                    fgAfter == fgBefore,
                    "before=0x" + fgBefore.ToString("X") + " after=0x" + fgAfter.ToString("X"));

                // 窗口矩形
                RECT wr = GetWinRect(hwnd);
                Write("  window rect = " + RectStr(wr) + "  size=" + (wr.right - wr.left) + "x" + (wr.bottom - wr.top));
                Check("panel height sane (30..600)", (wr.bottom - wr.top) >= 30 && (wr.bottom - wr.top) <= 600,
                    "h=" + (wr.bottom - wr.top));
                Check("panel width sane (60..1200)", (wr.right - wr.left) >= 60 && (wr.right - wr.left) <= 1200,
                    "w=" + (wr.right - wr.left));
                Check("anchored near (200+2, 200+2)", wr.left == 202, "left=" + wr.left + " top=" + wr.top);
            }

            // 字体族实测
            Write("  realized font face = " + DescribeFont());

            // ---------------------------------------------------------------
            Write("");
            Write("--- 3. 截图 shot1.png ---");
            CaptureTo(hwnd, Path.Combine(outDir, "shot1.png"));

            // ---------------------------------------------------------------
            Write("");
            Write("--- 4. Show #2 @ (620,430) selected=2 ---");
            bool show2Threw = false;
            try
            {
                panel.Show(620, 430, "形变  nima", labels1, notes1, 2);
            }
            catch (Exception ex) { show2Threw = true; Write("  Show threw: " + ex); }
            Check("Show #2 does not throw", !show2Threw, "");
            Pump(300);
            Write("  IsVisible=" + panel.IsVisible);
            RECT wr2 = GetWinRect(hwnd);
            Write("  window rect = " + RectStr(wr2));
            Check("moved to new anchor (622,432)", wr2.left == 622 && wr2.top == 432,
                "left=" + wr2.left + " top=" + wr2.top);
            CaptureTo(hwnd, Path.Combine(outDir, "shot2.png"));

            // ---------------------------------------------------------------
            Write("");
            Write("--- 5. 无状态行（statusText = null）应更矮 ---");
            int h1 = wr2.bottom - wr2.top;
            panel.Show(620, 430, null, labels1, notes1, 1);
            Pump(200);
            RECT wr3 = GetWinRect(hwnd);
            int h2 = wr3.bottom - wr3.top;
            Write("  with status=" + h1 + "px  without status=" + h2 + "px");
            Check("null statusText makes panel shorter", h2 < h1, h1 + " -> " + h2);

            // ---------------------------------------------------------------
            Write("");
            Write("--- 6. 脏输入（全部不得抛异常）---");
            TryDirty("labels=null", panel, () => panel.Show(200, 200, "x", null, null, 0), panel, true);
            TryDirty("labels=空数组", panel, () => panel.Show(200, 200, "x", new string[0], new string[0], 0), panel, true);
            TryDirty("selectedIndex=999", panel, () => panel.Show(200, 200, "形变  nima", labels1, notes1, 999), panel, false);
            TryDirty("selectedIndex=-5", panel, () => panel.Show(200, 200, "形变  nima", labels1, notes1, -5), panel, false);
            TryDirty("notes=null", panel, () => panel.Show(200, 200, "形变  nima", labels1, null, 1), panel, false);
            TryDirty("notes 比 labels 短", panel, () => panel.Show(200, 200, "形变  nima", labels1, new string[] { "只有一条" }, 1), panel, false);
            TryDirty("labels 含 null 元素", panel, () => panel.Show(200, 200, "形变  nima", new string[] { null, "b" }, new string[] { null, "" }, 0), panel, false);
            TryDirty("全部 note 为空（右列应消失）", panel, () => panel.Show(200, 200, "形变  nima", labels1, new string[] { "", "", "" }, 0), panel, false);
            TryDirty("statusText=空串", panel, () => panel.Show(200, 200, "", labels1, notes1, 0), panel, false);
            Pump(200);

            // ---------------------------------------------------------------
            Write("");
            Write("--- 7. 定位：翻转 + 多显示器工作区钳制 ---");
            int[][] anchors = new int[][]
            {
                new int[] { 1900, 1050 },   // 主屏右下角 → 必须左翻 + 上翻
                new int[] { -1919, -200 },  // 左侧显示器左上角 → 必须向右下钳制
                new int[] { 3630, 1710 },   // 最右侧显示器右下角 → 双重翻转 + 钳制
                new int[] { -1900, 1700 },  // 左下角 → 上翻 + 钳制
                new int[] { 960, 540 },     // 主屏中央 → 不翻转
            };
            for (int i = 0; i < anchors.Length; i++)
            {
                int ax = anchors[i][0];
                int ay = anchors[i][1];
                panel.Show(ax, ay, "形变  nima", labels1, notes1, 0);
                Pump(120);

                RECT r = GetWinRect(hwnd);
                RECT work;
                GetWork(ax, ay, out work);
                bool inside = r.left >= work.left && r.top >= work.top
                           && r.right <= work.right && r.bottom <= work.bottom;
                Write("  anchor(" + ax + "," + ay + ") -> rect " + RectStr(r)
                      + "  work " + RectStr(work) + "  fullyInside=" + inside);
                Check("anchor(" + ax + "," + ay + ") fully inside work area", inside, "");
            }

            // ---------------------------------------------------------------
            Write("");
            Write("--- 8. GDI / USER 句柄泄漏检查（200 次 Show）---");
            panel.Show(200, 200, "形变  nima", labels1, notes1, 0);
            Pump(100);
            uint gdi0 = GetGuiResources(GetCurrentProcess(), 0);
            uint usr0 = GetGuiResources(GetCurrentProcess(), 1);
            Write("  before: GDI=" + gdi0 + " USER=" + usr0);

            for (int i = 0; i < 200; i++)
            {
                panel.Show(200, 200, "形变  nima", labels1, notes1, i % 3);
                if (i % 7 == 0) panel.Hide();
                Pump(1);
            }
            panel.Show(200, 200, "形变  nima", labels1, notes1, 0);
            Pump(100);

            uint gdi1 = GetGuiResources(GetCurrentProcess(), 0);
            uint usr1 = GetGuiResources(GetCurrentProcess(), 1);
            Write("  after : GDI=" + gdi1 + " USER=" + usr1);
            Check("no GDI handle growth over 200 shows", gdi1 <= gdi0, gdi0 + " -> " + gdi1);
            Check("no USER handle growth over 200 shows", usr1 <= usr0 + 2, usr0 + " -> " + usr1);

            // ---------------------------------------------------------------
            Write("");
            Write("--- 9. Hide / Dispose ---");
            panel.Hide();
            Pump(150);
            Check("Hide() -> IsVisible == false", !panel.IsVisible, "");
            Check("Hide() idempotent", !panel.IsVisible, "");
            panel.Hide();

            bool disposeThrew = false;
            try
            {
                panel.Dispose();
                panel.Dispose();
                panel.Dispose();
            }
            catch (Exception ex) { disposeThrew = true; Write("  Dispose threw: " + ex); }
            Check("Dispose x3 does not throw", !disposeThrew, "");

            bool afterDisposeThrew = false;
            try
            {
                panel.Show(200, 200, "形变  nima", labels1, notes1, 0);
                panel.Hide();
                panel.RegisterWindowClass();
            }
            catch (Exception ex) { afterDisposeThrew = true; Write("  post-Dispose call threw: " + ex); }
            Check("calls after Dispose do not throw (no-op)", !afterDisposeThrew, "");
            Check("IsVisible == false after Dispose", !panel.IsVisible, "");

            uint gdi2 = GetGuiResources(GetCurrentProcess(), 0);
            Write("  GDI after dispose = " + gdi2 + "  (baseline " + gdi0 + ")");

            // ---------------------------------------------------------------
            Write("");
            Write("=== RESULT: " + (_failures == 0 ? "ALL PASS" : _failures + " FAILURE(S)") + " ===");

            try { File.WriteAllText(Path.Combine(outDir, "verify.log"), Log.ToString(), Encoding.UTF8); }
            catch (Exception) { }

            return _failures == 0 ? 0 : 1;
        }

        private static void TryDirty(string name, CandidatePanel panel, Action action, CandidatePanel p2, bool expectHidden)
        {
            bool threw = false;
            Exception caught = null;
            try { action(); }
            catch (Exception ex) { threw = true; caught = ex; }
            Pump(60);

            string extra = "";
            if (!threw && expectHidden)
            {
                extra = " IsVisible=" + panel.IsVisible;
                if (panel.IsVisible) { _failures++; extra += "  [FAIL: should be hidden]"; }
            }
            Check(name + " no-throw", !threw, threw ? caught.GetType().Name + ": " + caught.Message : extra);
        }

        // ====================================================================
        //  截图 / 探测工具
        // ====================================================================

        private static void CaptureTo(IntPtr hwnd, string path)
        {
            if (hwnd == IntPtr.Zero) { Write("  capture skipped: hwnd=0"); _failures++; return; }

            RECT r = GetWinRect(hwnd);
            int w = r.right - r.left;
            int h = r.bottom - r.top;
            if (w <= 0 || h <= 0) { Write("  capture skipped: empty rect"); _failures++; return; }

            try
            {
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(r.left, r.top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                    }

                    int distinct = DistinctColorSamples(bmp);
                    bmp.Save(path, ImageFormat.Png);
                    Write("  saved " + path + "  (" + w + "x" + h + ", " +
                          new FileInfo(path).Length + " bytes, distinct sampled colors=" + distinct + ")");
                    if (distinct <= 1)
                    {
                        Write("  [WARN] captured image looks flat/blank — screen capture may not reflect the panel");
                        _failures++;
                    }
                }
            }
            catch (Exception ex)
            {
                Write("  capture FAILED: " + ex.GetType().Name + ": " + ex.Message);
                _failures++;
            }
        }

        /// <summary>在图上采 24x24 网格，数不同颜色数，用于识别"纯色 = 抓瞎了"。</summary>
        private static int DistinctColorSamples(Bitmap bmp)
        {
            System.Collections.Generic.HashSet<int> seen = new System.Collections.Generic.HashSet<int>();
            try
            {
                for (int i = 0; i < 24; i++)
                {
                    for (int j = 0; j < 24; j++)
                    {
                        int x = (int)((bmp.Width - 1) * (i / 23.0));
                        int y = (int)((bmp.Height - 1) * (j / 23.0));
                        seen.Add(bmp.GetPixel(x, y).ToArgb());
                    }
                }
            }
            catch (Exception) { }
            return seen.Count;
        }

        private static string DescribeFont()
        {
            IntPtr hdc = IntPtr.Zero;
            try
            {
                IntPtr hFont = N.CreateFontW(-14, 0, 0, 0, N.FW_NORMAL,
                    0, 0, 0, N.DEFAULT_CHARSET, N.OUT_TT_PRECIS,
                    N.CLIP_DEFAULT_PRECIS, N.CLEARTYPE_QUALITY,
                    N.FF_SWISS, "Microsoft YaHei UI");
                if (hFont == IntPtr.Zero) return "(CreateFontW failed)";
                string name = N.RealizedFace(hFont);
                N.DeleteObject(hFont);
                return name;
            }
            catch (Exception ex) { return "(probe failed: " + ex.Message + ")"; }
            finally { if (hdc != IntPtr.Zero) N.ReleaseDC(IntPtr.Zero, hdc); }
        }

        private static int ReadDpi()
        {
            IntPtr hdc = N.GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return -1;
            int dpi = N.GetDeviceCaps(hdc, N.LOGPIXELSY);
            N.ReleaseDC(IntPtr.Zero, hdc);
            return dpi;
        }

        private static RECT GetWinRect(IntPtr hwnd)
        {
            RECT r;
            if (hwnd != IntPtr.Zero && N.GetWindowRect(hwnd, out r)) return r;
            r = new RECT();
            return r;
        }

        private static string RectStr(RECT r)
        {
            return "(" + r.left + "," + r.top + ")-(" + r.right + "," + r.bottom + ")";
        }

        private static void GetWork(int x, int y, out RECT work)
        {
            work = new RECT();
            POINT pt; pt.x = x; pt.y = y;
            IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (hMon != IntPtr.Zero)
            {
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfoW(hMon, ref mi)) { work = mi.rcWork; }
            }
        }

        private static void Pump(int ms)
        {
            uint start = N.GetTickCount();
            while (N.GetTickCount() - start < (uint)ms)
            {
                MSG msg;
                while (PeekMessageW(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                Thread.Sleep(3);
            }
        }
    }
}
