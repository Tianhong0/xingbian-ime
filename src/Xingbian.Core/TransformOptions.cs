namespace Xingbian.Core
{
    /// <summary>
    /// 形变（一层 IDS 拆解）参数。
    ///
    /// 默认值：字间用空格分隔、字内部件直接相连 —— 即 <c>你妈</c> → <c>亻尔 女马</c>。
    /// 该类可变，但一旦交给 <see cref="XingbianEngine"/> 后，调用方若在多线程下修改，
    /// 需要自行保证可见性（引擎只做一次引用读取）。
    /// </summary>
    public sealed class TransformOptions
    {
        private string _charSeparator = " ";
        private string _componentSeparator = "";
        private bool _keepUndecodable = true;

        public TransformOptions()
        {
        }

        /// <summary>字与字之间的连接串，默认 " "。赋 null 视作空串。</summary>
        public string CharSeparator
        {
            get { return _charSeparator; }
            set { _charSeparator = value ?? string.Empty; }
        }

        /// <summary>一个字内部各部件之间的连接串，默认 ""。赋 null 视作空串。</summary>
        public string ComponentSeparator
        {
            get { return _componentSeparator; }
            set { _componentSeparator = value ?? string.Empty; }
        }

        /// <summary>不可拆解的字符是否原样保留，默认 true；false 时直接跳过该字。</summary>
        public bool KeepUndecodable
        {
            get { return _keepUndecodable; }
            set { _keepUndecodable = value; }
        }
    }
}
