using System;
using System.Collections.Generic;
using System.Text;

namespace Xingbian.Core
{
    /// <summary>
    /// 一种切分结果。
    /// <see cref="Ids"/>：各音节的 ID；当最后一个音节是「未打完的前缀」时，
    /// <see cref="Partial"/> = true，<see cref="Ids"/> 不含该前缀（查询时按 <see cref="LastToken"/> 展开）。
    /// </summary>
    internal sealed class Segmentation
    {
        public readonly List<int> Ids = new List<int>(4);
        public bool Partial;
        public string LastToken;

        /// <summary>已确定的完整音节个数（不含最后一个待展开的前缀）。</summary>
        public int Count
        {
            get { return Ids.Count; }
        }

        public string Describe(string[] syllables)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Ids.Count; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append('|');
                }

                sb.Append(syllables[Ids[i]]);
            }

            if (Partial)
            {
                if (sb.Length > 0)
                {
                    sb.Append('|');
                }

                sb.Append(LastToken).Append('*');
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// 裸小写字母串 → 音节序列。
    ///
    /// 规则（见任务约定）：
    ///   * 只有最后一个音节允许是「未打完的前缀」，其余必须是完整音节；
    ///   * 枚举所有合法切分，数量与搜索节点数都有上限，避免组合爆炸；
    ///   * 若最后一个音节本身就是完整音节，查询端直接用该切分；否则用它去展开
    ///     （"nim" → "ni" + 前缀 "m" → 展开成 ma / mai / man / ... 再查表）。
    ///
    /// 音节表与前缀表在构造后只读，可多线程并发调用 <see cref="Segment"/>。
    /// </summary>
    internal sealed class PinyinSegmenter
    {
        private const int MaxSyllableLength = 8;

        private readonly HashSet<string> _syllables;
        private readonly Dictionary<string, int> _syllableIds;
        private readonly Dictionary<string, int[]> _prefixes;
        private readonly int _maxSegmentations;
        private readonly int _nodeBudget;

        public PinyinSegmenter(string[] syllables, int maxSegmentations, int nodeBudget)
        {
            if (syllables == null || syllables.Length == 0)
            {
                throw new ArgumentException("音节表为空。", "syllables");
            }

            _maxSegmentations = maxSegmentations;
            _nodeBudget = nodeBudget;
            _syllables = new HashSet<string>(syllables.Length, StringComparer.Ordinal);
            _syllableIds = new Dictionary<string, int>(syllables.Length, StringComparer.Ordinal);

            Dictionary<string, List<int>> prefixes = new Dictionary<string, List<int>>(syllables.Length * 4, StringComparer.Ordinal);

            for (int i = 0; i < syllables.Length; i++)
            {
                string syllable = syllables[i];
                if (string.IsNullOrEmpty(syllable))
                {
                    continue;
                }

                _syllables.Add(syllable);
                if (!_syllableIds.ContainsKey(syllable))
                {
                    _syllableIds.Add(syllable, i);
                }

                int maxPrefix = syllable.Length < MaxSyllableLength ? syllable.Length : MaxSyllableLength;
                for (int len = 1; len <= maxPrefix; len++)
                {
                    string prefix = syllable.Substring(0, len);
                    List<int> bucket;
                    if (!prefixes.TryGetValue(prefix, out bucket))
                    {
                        bucket = new List<int>(4);
                        prefixes.Add(prefix, bucket);
                    }

                    bucket.Add(i);
                }
            }

            _prefixes = new Dictionary<string, int[]>(prefixes.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<int>> pair in prefixes)
            {
                _prefixes.Add(pair.Key, pair.Value.ToArray());
            }
        }

        public int SyllableCount
        {
            get { return _syllableIds.Count; }
        }

        /// <summary>取「所有以 prefix 开头的完整音节」的音节 ID；prefix 本身若也是完整音节则包含它自己。</summary>
        public bool TryGetExpansion(string prefix, out int[] syllableIds)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                syllableIds = null;
                return false;
            }

            return _prefixes.TryGetValue(prefix, out syllableIds);
        }

        /// <summary>枚举 input 的所有合法切分（受 maxSegmentations / nodeBudget 限制）。</summary>
        public List<Segmentation> Segment(string input)
        {
            List<Segmentation> results = new List<Segmentation>(8);
            if (string.IsNullOrEmpty(input))
            {
                return results;
            }

            List<string> tokens = new List<string>(8);
            int budget = _nodeBudget;
            Walk(input, 0, tokens, results, ref budget);
            return results;
        }

        private void Walk(string input, int position, List<string> tokens,
            List<Segmentation> results, ref int budget)
        {
            if (results.Count >= _maxSegmentations || budget <= 0)
            {
                return;
            }

            budget--;

            int length = input.Length;
            if (position == length)
            {
                return;
            }

            int maxLength = length - position;
            if (maxLength > MaxSyllableLength)
            {
                maxLength = MaxSyllableLength;
            }

            for (int len = 1; len <= maxLength; len++)
            {
                string token = input.Substring(position, len);
                int[] expansion;
                if (!_prefixes.TryGetValue(token, out expansion))
                {
                    // token 已经不是任何音节的前缀，更长的 token 也不会是。
                    break;
                }

                bool isLast = position + len == length;
                if (isLast)
                {
                    int exactId;
                    bool complete = _syllableIds.TryGetValue(token, out exactId);

                    // 变体一（仅在末音节是完整音节时）：当作「已经打完的音节」查表。
                    // 例："ni" → 查 "ni"；"nima" → 查 "ni ma"。
                    if (complete)
                    {
                        Segmentation exact = new Segmentation();
                        exact.Partial = false;
                        exact.LastToken = token;
                        for (int i = 0; i < tokens.Count; i++)
                        {
                            exact.Ids.Add(_syllableIds[tokens[i]]);
                        }

                        exact.Ids.Add(exactId);
                        results.Add(exact);
                    }

                    // 变体二：当作「还会继续打字的前缀」展开。完整音节的展开集天然包含它自己。
                    // 这条路径是增量输入能出候选的关键（"nim" → ni + m → ni ma / mai / man / ...）。
                    // 查询端只在变体一无所获时才回退到它，避免 "ni" 被 牛 / 您 等更长音节污染。
                    Segmentation partial = new Segmentation();
                    partial.Partial = true;
                    partial.LastToken = token;
                    for (int i = 0; i < tokens.Count; i++)
                    {
                        partial.Ids.Add(_syllableIds[tokens[i]]);
                    }

                    results.Add(partial);
                }
                else
                {
                    int id;
                    if (_syllableIds.TryGetValue(token, out id))
                    {
                        tokens.Add(token);
                        Walk(input, position + len, tokens, results, ref budget);
                        tokens.RemoveAt(tokens.Count - 1);
                    }
                }

                if (results.Count >= _maxSegmentations)
                {
                    return;
                }
            }
        }

        /// <summary>该串本身是否是一个完整音节。</summary>
        public bool IsCompleteSyllable(string token)
        {
            return !string.IsNullOrEmpty(token) && _syllables.Contains(token);
        }
    }
}
