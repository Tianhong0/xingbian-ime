using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Xingbian.Core
{
    /// <summary>
    /// xingbian.bin 格式错误：magic / version / 块目录 / 块内容不符合 docs/FORMAT.md。
    /// </summary>
    public sealed class DictFormatException : Exception
    {
        public DictFormatException(string message)
            : base(message)
        {
        }

        public DictFormatException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// xingbian.bin 读取端（严格按 docs/FORMAT.md 实现）。
    ///
    /// 全部小端；块 1 为 UTF-8 文本，其余块内字符为 UTF-16LE。
    /// 文件头 32 字节 + 块目录（每项 24 字节）+ 数据区（顺序不做要求，按目录定位）。
    ///
    /// 生命周期：<see cref="Open"/> 时一次性读入并解析全部块，之后所有公开成员只读，
    /// 可被多线程并发访问（输入法宿主会从 UI 线程与后台线程同时查询）。
    /// </summary>
    public sealed class DictFile : IDisposable
    {
        private const int HeaderSize = 32;
        private const int DirectoryEntrySize = 24;
        private const uint SupportedVersion = 1;

        private const uint BlockSyllables = 1;
        private const uint BlockWords = 2;
        private const uint BlockIds = 3;
        private const uint BlockMeta = 4;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private struct BlockEntry
        {
            public long Offset;
            public long Length;
        }

        private readonly object _ioLock = new object();
        private readonly string _path;
        private readonly Dictionary<uint, BlockEntry> _blocks;
        private readonly string[] _syllables;
        private readonly bool _syllablesSorted;
        private readonly Dictionary<int, string> _ids;
        private readonly bool _idsSorted;
        private readonly int _wordCount;
        private readonly string _metaJson;

        private FileStream _stream;
        private bool _disposed;

        private DictFile(string path, FileStream stream, Dictionary<uint, BlockEntry> blocks)
        {
            _path = path;
            _stream = stream;
            _blocks = blocks;

            bool syllablesSorted;
            _syllables = ParseSyllables(out syllablesSorted);
            _syllablesSorted = syllablesSorted;

            bool idsSorted;
            _ids = ParseIds(out idsSorted);
            _idsSorted = idsSorted;

            _wordCount = ParseWordCount();
            _metaJson = ParseMeta();
        }

        /// <summary>词典文件绝对路径。</summary>
        public string Path
        {
            get { return _path; }
        }

        /// <summary>块目录中的块数量。</summary>
        public int BlockCount
        {
            get { return _blocks.Count; }
        }

        /// <summary>blockId = 1：音节表；数组下标即音节 ID。只读，请勿修改返回的数组。</summary>
        public string[] Syllables
        {
            get { return _syllables; }
        }

        /// <summary>blockId = 2：词表条目数（不去重，含同词多音）。</summary>
        public int WordCount
        {
            get { return _wordCount; }
        }

        /// <summary>blockId = 4：元信息 JSON；块不存在时返回 null。</summary>
        public string MetaJson
        {
            get { return _metaJson; }
        }

        /// <summary>音节表是否满足 FORMAT.md 的「按字节序升序、无重复」（读取端不因此拒绝加载，仅作诊断）。</summary>
        public bool SyllablesSorted
        {
            get { return _syllablesSorted; }
        }

        /// <summary>IDS 表是否满足 FORMAT.md 的「按码点升序、无重复」（读取端不因此拒绝加载，仅作诊断）。</summary>
        public bool IdsSorted
        {
            get { return _idsSorted; }
        }

        /// <summary>
        /// 打开词典文件。magic / version / 块目录 / 块内容不合法时抛出
        /// <see cref="DictFormatException"/>，异常信息包含出错位置与期望值。
        /// </summary>
        public static DictFile Open(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path", "词典路径不能为空。");
            }

            if (!File.Exists(path))
            {
                throw new DictFormatException("词典文件不存在：" + path);
            }

            FileStream stream = null;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 128 * 1024);

                if (stream.Length < HeaderSize)
                {
                    throw new DictFormatException(string.Format(
                        "词典文件头不完整：期望至少 {0} 字节，实际 {1} 字节（文件 {2}）。",
                        HeaderSize, stream.Length, path));
                }

                byte[] header = new byte[HeaderSize];
                ReadFully(stream, header, 0, HeaderSize, "文件头");

                if (header[0] != (byte)'X' || header[1] != (byte)'B' || header[2] != (byte)'I' || header[3] != (byte)'M')
                {
                    throw new DictFormatException(string.Format(
                        "词典 magic 校验失败：期望 \"XBIM\"，实际 \"{0}\"（文件 {1}）。该文件不是形变输入法词典。",
                        DescribeBytes(header, 0, 4), path));
                }

                uint version = ReadU32(header, 4);
                if (version != SupportedVersion)
                {
                    throw new DictFormatException(string.Format(
                        "词典 version 不受支持：期望 {0}，实际 {1}（文件 {2}）。请重新生成词典或升级读取端。",
                        SupportedVersion, version, path));
                }

                uint blockCount = ReadU32(header, 8);
                if (blockCount == 0 || blockCount > 64)
                {
                    throw new DictFormatException(string.Format(
                        "词典块数量异常：blockCount = {0}（文件 {1}）。", blockCount, path));
                }

                long directoryBytes = (long)blockCount * DirectoryEntrySize;
                if (HeaderSize + directoryBytes > stream.Length)
                {
                    throw new DictFormatException(string.Format(
                        "词典块目录越界：需要 {0} 字节，文件只有 {1} 字节（文件 {2}）。",
                        HeaderSize + directoryBytes, stream.Length, path));
                }

                byte[] directory = new byte[directoryBytes];
                ReadFully(stream, directory, 0, directory.Length, "块目录");

                Dictionary<uint, BlockEntry> blocks = new Dictionary<uint, BlockEntry>((int)blockCount);
                for (int i = 0; i < blockCount; i++)
                {
                    int at = i * DirectoryEntrySize;
                    uint blockId = ReadU32(directory, at);
                    ulong offset = ReadU64(directory, at + 8);
                    ulong length = ReadU64(directory, at + 16);

                    if (offset > (ulong)stream.Length || length > (ulong)stream.Length - offset)
                    {
                        throw new DictFormatException(string.Format(
                            "词典块目录第 {0} 项越界：blockId = {1}, offset = {2}, length = {3}，文件长度 {4}（文件 {5}）。",
                            i, blockId, offset, length, stream.Length, path));
                    }

                    if (blocks.ContainsKey(blockId))
                    {
                        throw new DictFormatException(string.Format(
                            "词典块目录存在重复的 blockId = {0}（文件 {1}）。", blockId, path));
                    }

                    BlockEntry entry;
                    entry.Offset = (long)offset;
                    entry.Length = (long)length;
                    blocks.Add(blockId, entry);
                }

                foreach (uint required in new uint[] { BlockSyllables, BlockWords, BlockIds })
                {
                    if (!blocks.ContainsKey(required))
                    {
                        throw new DictFormatException(string.Format(
                            "词典缺少必需的 blockId = {0}（文件 {1}）。", required, path));
                    }
                }

                DictFile file = null;
                try
                {
                    file = new DictFile(path, stream, blocks);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }

                return file;
            }
            catch
            {
                if (stream != null)
                {
                    stream.Dispose();
                }

                throw;
            }
        }

        /// <summary>
        /// 查询某个 Unicode 码点的一级 IDS 拆解结果。
        /// 表里不存在、或条目显式标记为不可拆（compLen == 0）时返回 false 且 components = null。
        /// </summary>
        public bool TryGetIds(int codepoint, out string components)
        {
            string value;
            if (_ids.TryGetValue(codepoint, out value) && value.Length > 0)
            {
                components = value;
                return true;
            }

            components = null;
            return false;
        }

        /// <summary>查询块在文件中的绝对偏移与长度（诊断用）。</summary>
        public bool TryGetBlock(uint blockId, out long offset, out long length)
        {
            BlockEntry entry;
            if (_blocks.TryGetValue(blockId, out entry))
            {
                offset = entry.Offset;
                length = entry.Length;
                return true;
            }

            offset = 0;
            length = 0;
            return false;
        }

        /// <summary>按目录读取整块原始字节。</summary>
        public byte[] ReadBlock(uint blockId)
        {
            BlockEntry entry;
            if (!_blocks.TryGetValue(blockId, out entry))
            {
                throw new DictFormatException(string.Format(
                    "词典缺少 blockId = {0}（文件 {1}）。", blockId, _path));
            }

            if (entry.Length > int.MaxValue)
            {
                throw new DictFormatException(string.Format(
                    "词典块 blockId = {0} 过大：{1} 字节（文件 {2}）。", blockId, entry.Length, _path));
            }

            byte[] buffer = new byte[entry.Length];
            lock (_ioLock)
            {
                if (_disposed || _stream == null)
                {
                    throw new ObjectDisposedException("DictFile", "词典已释放，无法读取块数据。");
                }

                _stream.Position = entry.Offset;
                ReadFully(_stream, buffer, 0, buffer.Length,
                    string.Format("blockId={0} 的块", blockId));
            }

            return buffer;
        }

        public void Dispose()
        {
            lock (_ioLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_stream != null)
                {
                    _stream.Dispose();
                    _stream = null;
                }
            }
        }

        // ---------------------------------------------------------------- 解析

        private string[] ParseSyllables(out bool sorted)
        {
            byte[] raw = ReadBlock(BlockSyllables);
            string text = Utf8.GetString(raw);

            List<string> lines = new List<string>(512);
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '\n')
                {
                    int end = i;
                    if (end > start && text[end - 1] == '\r')
                    {
                        end--;
                    }

                    if (end > start)
                    {
                        string line = text.Substring(start, end - start);
                        for (int k = 0; k < line.Length; k++)
                        {
                            char c = line[k];
                            if (c < 'a' || c > 'z')
                            {
                                throw new DictFormatException(string.Format(
                                    "音节表第 {0} 行含非法字符 U+{1:X4}（只允许小写 a-z）：\"{2}\"（文件 {3}）。",
                                    lines.Count, (int)c, line, _path));
                            }
                        }

                        lines.Add(line);
                    }

                    start = i + 1;
                }
            }

            if (lines.Count == 0)
            {
                throw new DictFormatException("音节表为空（blockId = 1）。文件：" + _path);
            }

            sorted = true;
            for (int i = 1; i < lines.Count; i++)
            {
                if (string.CompareOrdinal(lines[i - 1], lines[i]) >= 0)
                {
                    sorted = false;
                    break;
                }
            }

            return lines.ToArray();
        }

        private Dictionary<int, string> ParseIds(out bool sorted)
        {
            byte[] raw = ReadBlock(BlockIds);
            if (raw.Length < 4)
            {
                throw new DictFormatException("IDS 块长度不足 4 字节，缺少 count 字段。文件：" + _path);
            }

            uint count = ReadU32(raw, 0);
            Dictionary<int, string> map = new Dictionary<int, string>(
                (int)Math.Min(count, 1u << 17));

            int offset = 4;
            int previous = -1;
            sorted = true;

            for (uint i = 0; i < count; i++)
            {
                if (offset + 6 > raw.Length)
                {
                    throw new DictFormatException(string.Format(
                        "IDS 块在读取第 {0} / {1} 条时提前结束（偏移 {2}，块长 {3}）。文件 {4}。",
                        i, count, offset, raw.Length, _path));
                }

                int codepoint = (int)ReadU32(raw, offset);
                offset += 4;
                int compLength = ReadU16(raw, offset);
                offset += 2;

                if (offset + compLength * 2 > raw.Length)
                {
                    throw new DictFormatException(string.Format(
                        "IDS 块第 {0} 条（U+{1:X4}）的部件数据越界：需要 {2} 字节，剩余 {3} 字节。文件 {4}。",
                        i, codepoint, compLength * 2, raw.Length - offset, _path));
                }

                if (compLength > 0)
                {
                    map[codepoint] = Encoding.Unicode.GetString(raw, offset, compLength * 2);
                    offset += compLength * 2;
                }
                else
                {
                    // compLen == 0 表示显式标记为不可拆，读取端视同不存在。
                    map.Remove(codepoint);
                }

                if (i > 0 && codepoint <= previous)
                {
                    sorted = false;
                }

                previous = codepoint;
            }

            return map;
        }

        private int ParseWordCount()
        {
            BlockEntry entry;
            if (!_blocks.TryGetValue(BlockWords, out entry))
            {
                throw new DictFormatException("词典缺少词表块（blockId = 2）。文件：" + _path);
            }

            if (entry.Length < 4)
            {
                throw new DictFormatException("词表块长度不足 4 字节，缺少 count 字段。文件：" + _path);
            }

            byte[] buffer = new byte[4];
            lock (_ioLock)
            {
                if (_stream == null)
                {
                    throw new ObjectDisposedException("DictFile");
                }

                _stream.Position = entry.Offset;
                ReadFully(_stream, buffer, 0, 4, "词表 count");
            }

            uint count = ReadU32(buffer, 0);
            if (count > int.MaxValue)
            {
                throw new DictFormatException(string.Format(
                    "词表条目数异常：count = {0}。文件：{1}", count, _path));
            }

            return (int)count;
        }

        private string ParseMeta()
        {
            BlockEntry entry;
            if (!_blocks.TryGetValue(BlockMeta, out entry))
            {
                return null;
            }

            if (entry.Length == 0)
            {
                return string.Empty;
            }

            return Utf8.GetString(ReadBlock(BlockMeta));
        }

        // ---------------------------------------------------------------- 底层读取

        private static void ReadFully(Stream stream, byte[] buffer, int offset, int count, string what)
        {
            int done = 0;
            while (done < count)
            {
                int read = stream.Read(buffer, offset + done, count - done);
                if (read <= 0)
                {
                    throw new DictFormatException(string.Format(
                        "读取{0}时文件提前结束：期望 {1} 字节，实得 {2} 字节。", what, count, done));
                }

                done += read;
            }
        }

        internal static uint ReadU32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] | (buffer[offset + 1] << 8) |
                          (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
        }

        internal static ushort ReadU16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        private static ulong ReadU64(byte[] buffer, int offset)
        {
            return (ulong)ReadU32(buffer, offset) | ((ulong)ReadU32(buffer, offset + 4) << 32);
        }

        private static string DescribeBytes(byte[] buffer, int offset, int count)
        {
            StringBuilder sb = new StringBuilder(count * 4);
            for (int i = 0; i < count; i++)
            {
                byte b = buffer[offset + i];
                if (b >= 0x20 && b < 0x7F)
                {
                    sb.Append((char)b);
                }
                else
                {
                    sb.Append("\\x").Append(b.ToString("X2"));
                }
            }

            return sb.ToString();
        }
    }
}
