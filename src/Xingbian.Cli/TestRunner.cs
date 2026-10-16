using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Xingbian.Cli
{
    /// <summary>断言失败：消息会作为 --selftest 的 [FAIL] 原因打印。</summary>
    internal sealed class CheckFailedException : Exception
    {
        public CheckFailedException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// 极简断言 + 计数框架。每项打印 <c>[ ok ] 第 N 项: 说明</c> 或 <c>[FAIL] 第 N 项: 原因</c>。
    /// </summary>
    internal sealed class TestRunner
    {
        private readonly StringBuilder _report = new StringBuilder();
        private int _index;
        private int _failed;

        public int Total
        {
            get { return _index; }
        }

        public int Failed
        {
            get { return _failed; }
        }

        public bool AllPassed
        {
            get { return _failed == 0; }
        }

        public string Report
        {
            get { return _report.ToString(); }
        }

        /// <summary>运行一项检查；body 抛 <see cref="CheckFailedException"/> 或其它异常即判失败。</summary>
        public void Run(string description, Action body)
        {
            _index++;
            try
            {
                body();
                Write("[ ok ] 第 " + _index + " 项: " + description);
            }
            catch (Exception ex)
            {
                _failed++;
                string reason = ex is CheckFailedException
                    ? ex.Message
                    : ex.GetType().Name + ": " + ex.Message;
                Write("[FAIL] 第 " + _index + " 项: " + reason);
            }
        }

        private void Write(string line)
        {
            _report.Append(line).Append(Environment.NewLine);
            Console.WriteLine(line);
        }

        // ------------------------------------------------------------ 断言

        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new CheckFailedException(message);
            }
        }

        public static void Equal(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new CheckFailedException(string.Format(
                    "{0} 不符：期望 [{1}]，实际 [{2}]", what, Show(expected), Show(actual)));
            }
        }

        public static void Equal(int expected, int actual, string what)
        {
            if (expected != actual)
            {
                throw new CheckFailedException(string.Format(
                    "{0} 不符：期望 {1}，实际 {2}", what, expected, actual));
            }
        }

        public static void NotNull(object value, string what)
        {
            if (value == null)
            {
                throw new CheckFailedException(what + " 为 null。");
            }
        }

        public static void NotEmpty<T>(ICollection<T> items, string what)
        {
            if (items == null || items.Count == 0)
            {
                throw new CheckFailedException(what + " 为空。");
            }
        }

        /// <summary>把不可见字符转义，便于在失败信息里看清差异。</summary>
        public static string Show(string value)
        {
            if (value == null)
            {
                return "<null>";
            }

            StringBuilder sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r')
                {
                    sb.Append("\\r");
                }
                else if (c == '\n')
                {
                    sb.Append("\\n");
                }
                else if (c == '\t')
                {
                    sb.Append("\\t");
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }
}
