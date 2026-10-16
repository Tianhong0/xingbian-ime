using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Xingbian.Core
{
    /// <summary>词表里的一条记录（去重后同一个 key 下 Word 唯一）。</summary>
    internal struct WordRecord
    {
        public string Word;
        public int Freq;
    }

    /// <summary>
    /// blockId = 2 的查询索引：key = 音节 ID 序列用单个空格连接（例如 "12 45"），
    /// value = 该读音下的词条，按 freq 降序排列，且同一个 Word 只保留 freq 最高的一条。
    ///
    /// 加载完成后不再修改，可被多线程并发读取。
    /// </summary>
    internal sealed class WordIndex
    {
        private readonly Dictionary<string, List<WordRecord>> _map;
        private readonly int _entryCount;

        private WordIndex(Dictionary<string, List<WordRecord>> map, int entryCount)
        {
            _map = map;
            _entryCount = entryCount;
        }

        /// <summary>原始词表条目数（未去重）。</summary>
        public int EntryCount
        {
            get { return _entryCount; }
        }

        /// <summary>去重后的读音 key 数量。</summary>
        public int KeyCount
        {
            get { return _map.Count; }
        }

        /// <summary>把音节 ID 序列拼成 key。加载端与查询端必须使用同一个实现。</summary>
        public static string BuildKey(int[] ids, int count)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            if (count == 1)
            {
                return ids[0].ToString(CultureInfo.InvariantCulture);
            }

            StringBuilder sb = new StringBuilder(count * 4);
            sb.Append(ids[0].ToString(CultureInfo.InvariantCulture));
            for (int i = 1; i < count; i++)
            {
                sb.Append(' ').Append(ids[i].ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        public bool TryGet(string key, out List<WordRecord> records)
        {
            return _map.TryGetValue(key, out records);
        }

        public static WordIndex Build(byte[] block, int syllableCount)
        {
            if (block == null || block.Length < 4)
            {
                throw new DictFormatException("词表块长度不足 4 字节，缺少 count 字段。");
            }

            uint count = DictFile.ReadU32(block, 0);
            Dictionary<string, List<WordRecord>> map =
                new Dictionary<string, List<WordRecord>>((int)Math.Min(count, 1u << 17), StringComparer.Ordinal);

            int offset = 4;
            int[] syllableIds = new int[64];

            for (uint i = 0; i < count; i++)
            {
                if (offset + 8 > block.Length)
                {
                    throw new DictFormatException(string.Format(
                        "词表在读取第 {0} / {1} 条时提前结束（偏移 {2}，块长 {3}）。",
                        i, count, offset, block.Length));
                }

                int wordLength = DictFile.ReadU16(block, offset);
                offset += 2;
                int syllableLength = DictFile.ReadU16(block, offset);
                offset += 2;
                uint rawFreq = DictFile.ReadU32(block, offset);
                offset += 4;

                if (wordLength < 1)
                {
                    throw new DictFormatException(string.Format(
                        "词表第 {0} 条的 wordLen = 0，违反 FORMAT.md（wordLen >= 1）。", i));
                }

                if (syllableLength < 1 || syllableLength > syllableIds.Length)
                {
                    throw new DictFormatException(string.Format(
                        "词表第 {0} 条的 sylLen = {1} 非法（应在 1..{2}）。",
                        i, syllableLength, syllableIds.Length));
                }

                if (offset + syllableLength * 2 + wordLength * 2 > block.Length)
                {
                    throw new DictFormatException(string.Format(
                        "词表第 {0} 条的 syllable/word 数据越界（偏移 {1}，块长 {2}）。",
                        i, offset, block.Length));
                }

                for (int k = 0; k < syllableLength; k++)
                {
                    int id = DictFile.ReadU16(block, offset);
                    offset += 2;
                    if (id >= syllableCount)
                    {
                        throw new DictFormatException(string.Format(
                            "词表第 {0} 条的 syllableId = {1} 超出音节表范围（行数 {2}）。",
                            i, id, syllableCount));
                    }

                    syllableIds[k] = id;
                }

                string word = Encoding.Unicode.GetString(block, offset, wordLength * 2);
                offset += wordLength * 2;

                string key = BuildKey(syllableIds, syllableLength);
                List<WordRecord> list;
                if (!map.TryGetValue(key, out list))
                {
                    list = new List<WordRecord>(2);
                    map.Add(key, list);
                }

                WordRecord record;
                record.Word = word;
                record.Freq = rawFreq > int.MaxValue ? int.MaxValue : (int)rawFreq;
                list.Add(record);
            }

            foreach (KeyValuePair<string, List<WordRecord>> pair in map)
            {
                Normalize(pair.Value);
            }

            return new WordIndex(map, (int)count);
        }

        /// <summary>同一 key 下：按 Word 去重保留最高词频，再按 freq 降序（同频按词序）稳定排列。</summary>
        private static void Normalize(List<WordRecord> list)
        {
            if (list.Count > 1)
            {
                Deduplicate(list);
                list.Sort(CompareByFreqDesc);
            }
        }

        private static void Deduplicate(List<WordRecord> list)
        {
            if (list.Count <= 8)
            {
                // 绝大多数读音下只有 1~3 条，O(n^2) 比建字典更省。
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = list.Count - 1; j > i; j--)
                    {
                        if (string.Equals(list[i].Word, list[j].Word, StringComparison.Ordinal))
                        {
                            if (list[j].Freq > list[i].Freq)
                            {
                                WordRecord keep = list[i];
                                keep.Freq = list[j].Freq;
                                list[i] = keep;
                            }

                            list.RemoveAt(j);
                        }
                    }
                }

                return;
            }

            Dictionary<string, int> seen = new Dictionary<string, int>(list.Count, StringComparer.Ordinal);
            List<WordRecord> kept = new List<WordRecord>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                WordRecord record = list[i];
                int previous;
                if (seen.TryGetValue(record.Word, out previous))
                {
                    if (record.Freq > kept[previous].Freq)
                    {
                        kept[previous] = record;
                    }

                    continue;
                }

                seen.Add(record.Word, kept.Count);
                kept.Add(record);
            }

            list.Clear();
            list.AddRange(kept);
        }

        private static int CompareByFreqDesc(WordRecord a, WordRecord b)
        {
            int byFreq = b.Freq.CompareTo(a.Freq);
            if (byFreq != 0)
            {
                return byFreq;
            }

            return string.CompareOrdinal(a.Word, b.Word);
        }
    }
}
