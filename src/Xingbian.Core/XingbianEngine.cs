using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Xingbian.Core
{
    /// <summary>
    /// 形变输入法引擎：词典读取 + 拼音切分 + 候选查询 + 形变。
    ///
    /// 线程安全：<see cref="Open"/> / <see cref="CreateDefault"/> 完成后，所有
    /// <see cref="GetCandidates"/> / <see cref="Transform"/> 调用只读取不可变状态，
    /// 无需加锁即可被输入法宿主的多线程并发调用。
    /// </summary>
    public sealed class XingbianEngine : IDisposable
    {
        /// <summary>调用方传入 max &lt;= 0 时使用的默认候选上限。</summary>
        public const int DefaultMaxCandidates = 20;

        private const int MaxSegmentationCount = 64;
        private const int MaxSegmentNodes = 4096;
        private const int MaxQuerySyllables = 16;
        private const int MaxCollectedWords = 4096;

        private const string DictFileName = "xingbian.bin";
        private const string DictSubDirectory = "dict";
        private const string DictEnvironmentVariable = "XINGBIAN_DICT";
        private const string DictRegistrySubKey = @"Software\XingbianIME";
        private const string DictRegistryValue = "DictPath";

        private readonly DictFile _dict;
        private readonly WordIndex _index;
        private readonly PinyinSegmenter _segmenter;
        private readonly string _dictPath;
        private readonly List<string> _pathCandidates;
        private readonly List<string> _loadWarnings;

        private TransformOptions _options;
        private bool _disposed;

        private XingbianEngine(DictFile dict, string dictPath, List<string> pathCandidates, List<string> loadWarnings)
        {
            _dict = dict;
            _dictPath = dictPath;
            _pathCandidates = pathCandidates;
            _loadWarnings = loadWarnings;
            _segmenter = new PinyinSegmenter(dict.Syllables, MaxSegmentationCount, MaxSegmentNodes);
            _index = WordIndex.Build(dict.ReadBlock(2), dict.Syllables.Length);
            _options = new TransformOptions();
        }

        /// <summary>打开指定路径的词典。格式非法时抛出 <see cref="DictFormatException"/>。</summary>
        public static XingbianEngine Open(string dictPath)
        {
            if (string.IsNullOrEmpty(dictPath))
            {
                throw new ArgumentNullException("dictPath", "词典路径不能为空。");
            }

            string fullPath = Path.GetFullPath(dictPath);
            DictFile dict = DictFile.Open(fullPath);
            try
            {
                return new XingbianEngine(dict, fullPath, new List<string> { fullPath }, new List<string>());
            }
            catch
            {
                dict.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 按以下顺序查找词典并打开：
        ///   1. 环境变量 XINGBIAN_DICT
        ///   2. 注册表 HKCU\Software\XingbianIME\DictPath
        ///   3. 程序集所在目录\dict\xingbian.bin
        ///   4. 程序集所在目录\xingbian.bin
        /// 都找不到时抛出 <see cref="FileNotFoundException"/>，信息中列出全部已尝试路径。
        /// </summary>
        public static XingbianEngine CreateDefault()
        {
            List<string> tried = new List<string>();
            List<string> warnings = new List<string>();

            CollectCandidates(tried, warnings, ResolveBaseDirectory());

            for (int i = 0; i < tried.Count; i++)
            {
                string candidate = tried[i];
                if (!File.Exists(candidate))
                {
                    continue;
                }

                DictFile dict = DictFile.Open(candidate);
                try
                {
                    return new XingbianEngine(dict, candidate, tried, warnings);
                }
                catch
                {
                    dict.Dispose();
                    throw;
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("未找到形变输入法词典 ").Append(DictFileName).Append("。已尝试以下路径：");
            for (int i = 0; i < tried.Count; i++)
            {
                sb.Append(Environment.NewLine).Append("  [").Append(i + 1).Append("] ").Append(tried[i]);
            }

            sb.Append(Environment.NewLine)
              .Append("修复方式：设置环境变量 ").Append(DictEnvironmentVariable)
              .Append("，或写入注册表 HKCU\\").Append(DictRegistrySubKey).Append('\\').Append(DictRegistryValue)
              .Append("，或把词典放到程序目录的 ").Append(DictSubDirectory).Append("\\ 子目录。");

            if (warnings.Count > 0)
            {
                sb.Append(Environment.NewLine).Append("查找过程中的异常：");
                for (int i = 0; i < warnings.Count; i++)
                {
                    sb.Append(Environment.NewLine).Append("  - ").Append(warnings[i]);
                }
            }

            throw new FileNotFoundException(sb.ToString(), DictFileName);
        }

        /// <summary>当前词典文件的绝对路径。</summary>
        public string DictPath
        {
            get { return _dictPath; }
        }

        /// <summary>CreateDefault 依次尝试过的路径（诊断用，只读）。</summary>
        public IList<string> ResolvedPathCandidates
        {
            get { return _pathCandidates.AsReadOnly(); }
        }

        /// <summary>查找 / 加载过程中的非致命异常描述（诊断用，只读）。</summary>
        public IList<string> LoadWarnings
        {
            get { return _loadWarnings.AsReadOnly(); }
        }

        /// <summary>词表原始条目数（同词多音未合并）。</summary>
        public int WordEntryCount
        {
            get { return _index.EntryCount; }
        }

        /// <summary>去重后的「读音序列」索引条数。</summary>
        public int IndexedKeyCount
        {
            get { return _index.KeyCount; }
        }

        /// <summary>形变参数。赋 null 抛 <see cref="ArgumentNullException"/>。</summary>
        public TransformOptions Options
        {
            get
            {
                TransformOptions options = _options;
                return options ?? new TransformOptions();
            }

            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException("value", "TransformOptions 不能为 null。");
                }

                _options = value;
            }
        }

        /// <summary>底层词典（只读；生命周期由引擎管理，调用方不要 Dispose）。</summary>
        public DictFile Dict
        {
            get
            {
                ThrowIfDisposed();
                return _dict;
            }
        }

        /// <summary>
        /// 查询候选。rawPinyin 是裸小写字母串（如 "nima"、"zhongguo"），
        /// 末尾音节允许是尚未打完的前缀（如 "nim" → ni + m → 展开成 ma / mai / man / ... 再查）。
        /// 空输入、含非 a-z 字符、无匹配时一律返回空列表，不抛异常。
        /// 返回结果按词频降序，同一个 Word 只保留词频最高的一条。
        /// </summary>
        public IList<Candidate> GetCandidates(string rawPinyin, int max)
        {
            ThrowIfDisposed();

            List<Candidate> candidates = new List<Candidate>();
            if (string.IsNullOrEmpty(rawPinyin))
            {
                return candidates;
            }

            if (max <= 0)
            {
                max = DefaultMaxCandidates;
            }

            for (int i = 0; i < rawPinyin.Length; i++)
            {
                char c = rawPinyin[i];
                if (c < 'a' || c > 'z')
                {
                    return candidates;
                }
            }

            List<Segmentation> segmentations = _segmenter.Segment(rawPinyin);
            if (segmentations.Count == 0)
            {
                return candidates;
            }

            // Word → 最高词频。同一读音 key 内部已按 freq 降序，跨读音再取 max。
            Dictionary<string, int> best = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, bool> visitedKeys = new Dictionary<string, bool>(StringComparer.Ordinal);
            List<string> keys = new List<string>(16);
            int[] idBuffer = new int[MaxQuerySyllables];

            // 两趟查询：
            //   第一趟只查「末音节当作已打完」的精确切分（"ni" → 查 "ni"，"nima" → 查 "ni ma"）；
            //   第一趟一无所获时才回退到「末音节当作前缀」展开（"nim" → ni + m → ni ma / mai / ...）。
            // 这样既保证 "ni" 只出 你 / 尼 / 泥 而不会被 牛 / 您 等更长音节污染，
            // 又保证 "nim"、"zhonggu" 这类打到一半的输入照样能出候选。
            // 真实音节表里 m / n / a / e / o 本身就是完整音节（"嗯" = n，"呣" = m），
            // 所以 "nim" 的第一趟会命中 "ni m" 这个不存在的 key，必须靠第二趟兜底。
            for (int pass = 0; pass < 2 && best.Count == 0; pass++)
            {
                bool expandLast = pass == 1;

                for (int s = 0; s < segmentations.Count; s++)
                {
                    Segmentation segmentation = segmentations[s];
                    if (segmentation.Partial != expandLast)
                    {
                        continue;
                    }

                    if (segmentation.Count + 1 > idBuffer.Length)
                    {
                        continue;
                    }

                    if (!segmentation.Partial)
                    {
                        AddKey(segmentation, -1, idBuffer, keys, visitedKeys);
                    }
                    else
                    {
                        int[] expansion;
                        if (!_segmenter.TryGetExpansion(segmentation.LastToken, out expansion))
                        {
                            continue;
                        }

                        for (int e = 0; e < expansion.Length; e++)
                        {
                            AddKey(segmentation, expansion[e], idBuffer, keys, visitedKeys);
                        }
                    }

                    if (keys.Count > 0)
                    {
                        Collect(keys, best);
                        keys.Clear();
                    }

                    if (best.Count >= MaxCollectedWords)
                    {
                        break;
                    }
                }
            }

            if (best.Count == 0)
            {
                return candidates;
            }

            List<KeyValuePair<string, int>> ordered = new List<KeyValuePair<string, int>>(best);
            ordered.Sort(CompareByFreqDesc);

            TransformOptions options = Options;
            int take = ordered.Count < max ? ordered.Count : max;
            for (int i = 0; i < take; i++)
            {
                string word = ordered[i].Key;
                candidates.Add(new Candidate(
                    word,
                    CharFormatter.Format(word, _dict, options),
                    ordered[i].Value,
                    CharFormatter.IsFullyDecomposable(word, _dict)));
            }

            return candidates;
        }

        /// <summary>
        /// 形变：把「你妈」变成「亻尔 女马」。
        /// 逐 UTF-16 码元处理，非 BMP 字符（代理对）原样输出。null / 空串返回空串。
        /// </summary>
        public string Transform(string text)
        {
            ThrowIfDisposed();
            return CharFormatter.Format(text, _dict, Options);
        }

        /// <summary>整体形变是否「每个字都能拆」。空串视为 true。</summary>
        public bool IsDecomposable(string text)
        {
            ThrowIfDisposed();
            return CharFormatter.IsFullyDecomposable(text, _dict);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dict.Dispose();
        }

        // ---------------------------------------------------------------- 内部

        private static void AddKey(Segmentation segmentation, int lastSyllableId, int[] buffer,
            List<string> keys, Dictionary<string, bool> visited)
        {
            List<int> ids = segmentation.Ids;
            int count = ids.Count;
            for (int i = 0; i < count; i++)
            {
                buffer[i] = ids[i];
            }

            if (lastSyllableId >= 0)
            {
                buffer[count] = lastSyllableId;
                count++;
            }

            string key = WordIndex.BuildKey(buffer, count);
            if (!visited.ContainsKey(key))
            {
                visited.Add(key, true);
                keys.Add(key);
            }
        }

        private void Collect(List<string> keys, Dictionary<string, int> best)
        {
            for (int k = 0; k < keys.Count; k++)
            {
                List<WordRecord> records;
                if (!_index.TryGet(keys[k], out records))
                {
                    continue;
                }

                for (int r = 0; r < records.Count; r++)
                {
                    WordRecord record = records[r];
                    int existing;
                    if (best.TryGetValue(record.Word, out existing))
                    {
                        if (record.Freq > existing)
                        {
                            best[record.Word] = record.Freq;
                        }

                        continue;
                    }

                    best.Add(record.Word, record.Freq);
                }
            }
        }

        private static int CompareByFreqDesc(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
        {
            if (a.Value != b.Value)
            {
                return b.Value.CompareTo(a.Value);
            }

            int byLength = a.Key.Length.CompareTo(b.Key.Length);
            if (byLength != 0)
            {
                return byLength;
            }

            return string.CompareOrdinal(a.Key, b.Key);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("XingbianEngine", "引擎已释放。");
            }
        }

        private static void CollectCandidates(List<string> tried, List<string> warnings, string baseDirectory)
        {
            string fromEnvironment = null;
            try
            {
                fromEnvironment = Environment.GetEnvironmentVariable(DictEnvironmentVariable);
            }
            catch (Exception ex)
            {
                warnings.Add("读取环境变量 " + DictEnvironmentVariable + " 失败：" + ex.Message);
            }

            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                tried.Add(Path.GetFullPath(fromEnvironment));
            }

            string fromRegistry = null;
            try
            {
                fromRegistry = ReadRegistryDictPath();
            }
            catch (Exception ex)
            {
                warnings.Add("读取注册表 HKCU\\" + DictRegistrySubKey + "\\" + DictRegistryValue + " 失败：" + ex.Message);
            }

            if (!string.IsNullOrEmpty(fromRegistry))
            {
                tried.Add(Path.GetFullPath(fromRegistry));
            }

            if (!string.IsNullOrEmpty(baseDirectory))
            {
                tried.Add(Path.Combine(baseDirectory, DictSubDirectory, DictFileName));
                tried.Add(Path.Combine(baseDirectory, DictFileName));
            }
        }

        /// <summary>
        /// 非 .NET Framework 宿主下 HKCU 不是有效配置单元，异常由外层转成 warning，不影响其余候选路径。
        /// </summary>
        private static string ReadRegistryDictPath()
        {
#if NETFRAMEWORK
            using (Microsoft.Win32.RegistryKey key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DictRegistrySubKey, false))
            {
                if (key == null)
                {
                    return null;
                }

                object value = key.GetValue(DictRegistryValue);
                return value == null ? null : value.ToString();
            }
#else
            return null;
#endif
        }

        private static string ResolveBaseDirectory()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(location))
                {
                    string directory = Path.GetDirectoryName(location);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        return directory;
                    }
                }
            }
            catch (Exception)
            {
                // 单文件 / 动态程序集等场景下 Location 可能为空，退回当前目录。
            }

            try
            {
                return Directory.GetCurrentDirectory();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
