namespace Xingbian.Core
{
    /// <summary>一条候选词：原词 + 形变结果 + 词频 + 是否全字可拆。</summary>
    public sealed class Candidate
    {
        private readonly string _word;
        private readonly string _transformed;
        private readonly int _freq;
        private readonly bool _decomposable;

        internal Candidate(string word, string transformed, int freq, bool decomposable)
        {
            _word = word;
            _transformed = transformed;
            _freq = freq;
            _decomposable = decomposable;
        }

        /// <summary>原词，例如 <c>你妈</c>。</summary>
        public string Word
        {
            get { return _word; }
        }

        /// <summary>形变结果（按查询时的 <see cref="XingbianEngine.Options"/> 现算），例如 <c>亻尔 女马</c>。</summary>
        public string Transformed
        {
            get { return _transformed; }
        }

        /// <summary>词频，越大越常用。</summary>
        public int Freq
        {
            get { return _freq; }
        }

        /// <summary>是否每个字都能做一级 IDS 拆解。</summary>
        public bool Decomposable
        {
            get { return _decomposable; }
        }

        public override string ToString()
        {
            return _word + "(" + _freq + ") -> " + _transformed;
        }
    }
}
