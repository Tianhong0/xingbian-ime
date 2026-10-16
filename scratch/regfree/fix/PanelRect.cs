// ============================================================================
//  PanelRect.cs —— 候选面板的屏幕矩形
//
//  为什么要自己定义一个矩形而不用 System.Drawing.Rectangle：
//    本 DLL 是被**任意宿主进程**（记事本 / 浏览器 / Office / 各种第三方软件）
//    进程内加载的 TSF 文本服务。System.Drawing 会把 GDI+ 拉进每一个加载我们的
//    进程，而 TSF 文本服务会被系统注入到大量进程中 —— 这是纯粹的负担，
//    并且在低完整性 / 沙箱化的宿主里可能加载失败。
//    CandidatePanel 的硬约束就是「只用 mscorlib」，所以这里自带一个四整数矩形。
//
//  语义与 System.Drawing.Rectangle 一致：
//    new PanelRect(x, y, w, h) → Left=x, Top=y, Right=x+w, Bottom=y+h
//    （即 Right/Bottom 是**开区间边界**，不含端点，与 Win32 RECT 的约定相同）
// ============================================================================

namespace Xingbian.Tsf
{
    /// <summary>屏幕坐标矩形。字段语义与 Win32 RECT 一致（Right/Bottom 为开边界）。</summary>
    internal struct PanelRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public PanelRect(int x, int y, int width, int height)
        {
            Left = x;
            Top = y;
            Right = x + width;
            Bottom = y + height;
        }

        public int Width
        {
            get { return Right - Left; }
        }

        public int Height
        {
            get { return Bottom - Top; }
        }

        public override string ToString()
        {
            return "[" + Left + "," + Top + " " + Width + "x" + Height + "]";
        }
    }
}
