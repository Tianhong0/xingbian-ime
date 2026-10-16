using System;
using System.Text;

namespace Xingbian.Core
{
    /// <summary>
    /// 字 → 形变串的公共实现。
    ///
    /// 码元级处理：
    ///   * 字符在 IDS 表里且可拆 → 各部分用 <see cref="TransformOptions.ComponentSeparator"/> 连接；
    ///   * 否则 → <see cref="TransformOptions.KeepUndecodable"/> ? 原字符 : 跳过；
    ///   * 字与字之间用 <see cref="TransformOptions.CharSeparator"/> 连接。
    ///
    /// 分隔符只在「两个都会输出的字之间」追加，所以结果不会有尾随 / 前导分隔符。
    /// 所有方法只读 <see cref="DictFile"/> 与 <see cref="TransformOptions"/>，可并发调用。
    /// </summary>
    internal static class CharFormatter
    {
        /// <summary>当前码元（或代理对）展开后是否会输出内容。</summary>
        public static bool IsOutput(char c, char next, DictFile dict, TransformOptions options)
        {
            if (char.IsHighSurrogate(c) && next != '\0' && char.IsLowSurrogate(next))
            {
                // 非 BMP 字符（扩展区汉字、emoji 等）一律不拆，原样输出或整体跳过。
                return options.KeepUndecodable;
            }

            string components;
            if (dict != null && dict.TryGetIds(c, out components))
            {
                return true;
            }

            return options.KeepUndecodable;
        }

        /// <summary>把单个码元（或代理对）追加到输出（不含字间分隔符）。</summary>
        public static void Append(ref StringBuilder sb, char c, char next, DictFile dict, TransformOptions options)
        {
            if (char.IsHighSurrogate(c) && next != '\0' && char.IsLowSurrogate(next))
            {
                if (options.KeepUndecodable)
                {
                    sb.Append(c).Append(next);
                }

                return;
            }

            string components;
            if (dict != null && dict.TryGetIds(c, out components))
            {
                string separator = options.ComponentSeparator;
                for (int i = 0; i < components.Length; i++)
                {
                    if (i > 0 && separator.Length > 0)
                    {
                        sb.Append(separator);
                    }

                    sb.Append(components[i]);
                }

                return;
            }

            if (options.KeepUndecodable)
            {
                sb.Append(c);
            }
        }

        /// <summary>整体形变。null / 空串返回空串。</summary>
        public static string Format(string text, DictFile dict, TransformOptions options)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string separator = options.CharSeparator;
            StringBuilder sb = new StringBuilder(text.Length * 3);
            bool needSeparator = false;

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';
                bool paired = char.IsLowSurrogate(next) && char.IsHighSurrogate(c);
                int step = paired ? 2 : 1;

                if (!IsOutput(c, next, dict, options))
                {
                    i += step;
                    continue;
                }

                if (needSeparator && separator.Length > 0)
                {
                    sb.Append(separator);
                }

                Append(ref sb, c, next, dict, options);
                needSeparator = true;
                i += step;
            }

            return sb.ToString();
        }

        /// <summary>「是否每个字都能拆」：空串视为可拆；非 BMP 字符视为不可拆。</summary>
        public static bool IsFullyDecomposable(string text, DictFile dict)
        {
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }

            if (dict == null)
            {
                return false;
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    return false;
                }

                string components;
                if (!dict.TryGetIds(c, out components))
                {
                    return false;
                }

                i++;
            }

            return true;
        }
    }
}
