// ============================================================================
//  PanelItem.cs
//  —— 候选词面板的【显示数据模型】
//
//  职责：只承载"一条候选该怎么显示"，不含任何 Win32 / 布局 / 业务逻辑。
//  与 Xingbian.Core.Candidate 的区别：Candidate 是引擎产出的语义对象，
//  PanelItem 是 UI 层的展示快照（已形变 + 已排版），两者刻意解耦 ——
//  这样 CandidatePanel 不必引用 Xingbian.Core，可被独立测试。
// ============================================================================

using System;

namespace Xingbian.Tsf
{
    /// <summary>
    /// 一条候选的显示数据。
    /// 不可变：构造后 Display / Annotation 不再改变（面板绘制期间可能跨线程读取，
    /// 保持不可变即可免锁）。
    /// </summary>
    internal sealed class PanelItem
    {
        private readonly string _display;
        private readonly string _annotation;

        /// <summary>
        /// 构造一条候选显示数据。
        /// </summary>
        /// <param name="display">
        /// 主显示文本（已形变），例如 "亻尔 女马"。允许 null，绘制时按空串处理。
        /// </param>
        /// <param name="annotation">
        /// 次要说明，例如 "你妈"；允许 null 或空串，表示"无说明，不绘制右栏"。
        /// </param>
        public PanelItem(string display, string annotation)
        {
            _display = display ?? string.Empty;
            _annotation = annotation ?? string.Empty;
        }

        /// <summary>主显示文本，已形变，例如 "亻尔 女马"。永不为 null（最多为空串）。</summary>
        public string Display
        {
            get { return _display; }
        }

        /// <summary>次要说明，例如 "你妈" 或 ""。永不为 null；空串表示无说明。</summary>
        public string Annotation
        {
            get { return _annotation; }
        }

        /// <summary>是否有需要绘制的说明文本。</summary>
        internal bool HasAnnotation
        {
            get { return _annotation.Length > 0; }
        }

        /// <summary>调试用："显示(说明)"。</summary>
        public override string ToString()
        {
            return _annotation.Length > 0
                ? _display + "(" + _annotation + ")"
                : _display;
        }
    }
}
