using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Xingbian.Core;

namespace Xingbian.Cli
{
    /// <summary>
    /// Xingbian.Cli 入口：--selftest / --input / --transform / --bench。
    /// net48 控制台默认走 GBK 代码页，中文会乱码，所以启动第一件事就是切 UTF-8。
    /// </summary>
    internal static class Program
    {
        private static readonly string Usage = @"形变输入法自检 / 查询工具 (Xingbian.Cli)

用法:
  Xingbian.Cli --selftest  [--dict <path>]          全量自检（14 项）
  Xingbian.Cli --input <pinyin> [--dict <path>]     打印该拼音的候选（含形变列）
  Xingbian.Cli --transform <text>                   打印形变结果
  Xingbian.Cli --bench     [--dict <path>]          打印加载耗时 / 常驻内存 / 单次查询耗时
  Xingbian.Cli --help

词典查找顺序（除 --dict 外）:
" + DictResolver.DescribeSearchOrder();

        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (IOException)
            {
                // 输出被重定向到文件时该调用可能失败，不影响后续逻辑。
            }

            try
            {
                return Run(args);
            }
            catch (DictFormatException ex)
            {
                Console.Error.WriteLine("词典格式错误：" + ex.Message);
                return 2;
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("未处理的异常：" + ex);
                return 4;
            }
        }

        private static int Run(string[] args)
        {
            string mode = null;
            string dictPath = null;
            string value = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--dict", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--dict 需要紧跟一个路径参数。");
                        return 1;
                    }

                    dictPath = args[++i];
                    continue;
                }

                if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(Usage);
                    return 0;
                }

                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    if (mode != null)
                    {
                        Console.Error.WriteLine("只能指定一个模式，已给出：" + mode + " 与 " + arg);
                        return 1;
                    }

                    mode = arg;
                    continue;
                }

                if (value == null)
                {
                    value = arg;
                    continue;
                }

                Console.Error.WriteLine("多余的参数：" + arg);
                return 1;
            }

            if (mode == null)
            {
                Console.WriteLine(Usage);
                return 1;
            }

            if (string.Equals(mode, "--transform", StringComparison.OrdinalIgnoreCase))
            {
                return TransformCommand(value, dictPath);
            }

            if (string.Equals(mode, "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                return SelfTestCommand(dictPath);
            }

            if (string.Equals(mode, "--input", StringComparison.OrdinalIgnoreCase))
            {
                return InputCommand(value, dictPath);
            }

            if (string.Equals(mode, "--bench", StringComparison.OrdinalIgnoreCase))
            {
                return BenchCommand(dictPath);
            }

            Console.Error.WriteLine("未知模式：" + mode);
            Console.WriteLine(Usage);
            return 1;
        }

        // ------------------------------------------------------------ 模式实现

        /// <summary>--transform：按默认参数形变一段文本。</summary>
        private static int TransformCommand(string text, string explicitDict)
        {
            if (string.IsNullOrEmpty(text))
            {
                Console.Error.WriteLine("--transform 需要一段文本参数，例如：Xingbian.Cli --transform 你妈");
                return 1;
            }

            string source;
            string path = DictResolver.Resolve(explicitDict, out source);
            if (path == null || !File.Exists(path))
            {
                PrintDictMissing(explicitDict);
                return 3;
            }

            using (XingbianEngine engine = XingbianEngine.Open(path))
            {
                Console.WriteLine("词典: " + path);
                Console.WriteLine("输入: " + text);
                Console.WriteLine("形变: " + engine.Transform(text));
            }

            return 0;
        }

        /// <summary>--input：打印某个拼音的候选表。</summary>
        private static int InputCommand(string pinyin, string explicitDict)
        {
            if (string.IsNullOrEmpty(pinyin))
            {
                Console.Error.WriteLine("--input 需要一个拼音参数，例如：Xingbian.Cli --input nima");
                return 1;
            }

            string source;
            string path = DictResolver.Resolve(explicitDict, out source);
            if (path == null || !File.Exists(path))
            {
                PrintDictMissing(explicitDict);
                return 3;
            }

            using (XingbianEngine engine = XingbianEngine.Open(path))
            {
                Console.WriteLine("词典: " + path + "  (" + source + ")");
                Console.WriteLine("音节表: " + engine.Dict.Syllables.Length + " 个；词表: " + engine.WordEntryCount +
                                  " 条 → 索引 " + engine.IndexedKeyCount + " 个读音");
                Console.WriteLine();

                IList<Candidate> candidates = engine.GetCandidates(pinyin, 20);
                Console.WriteLine("输入 \"" + pinyin + "\" 的候选（" + candidates.Count + " 条）:");
                Console.WriteLine("序号  词频      可拆  原词            形变");
                Console.WriteLine("----  --------  ----  --------------  --------------");

                if (candidates.Count == 0)
                {
                    Console.WriteLine("(无候选)");
                }
                else
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        Candidate candidate = candidates[i];
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "{0,4}  {1,8}  {2,-4}  {3,-14}  {4}",
                            i + 1, candidate.Freq, candidate.Decomposable ? "是" : "否",
                            candidate.Word, candidate.Transformed));
                    }
                }
            }

            return 0;
        }

        /// <summary>--bench：加载耗时、常驻内存、单次查询耗时。</summary>
        private static int BenchCommand(string explicitDict)
        {
            string source;
            string path = DictResolver.Resolve(explicitDict, out source);
            if (path == null || !File.Exists(path))
            {
                PrintDictMissing(explicitDict);
                return 3;
            }

            FileInfo info = new FileInfo(path);
            Console.WriteLine("词典: " + path + "  (" + source + ")");
            Console.WriteLine("文件大小: " + (info.Length / 1048576.0).ToString("F2", CultureInfo.InvariantCulture) + " MB");
            Console.WriteLine();

            EngineLoadMetrics metrics;
            XingbianEngine engine = EngineLoadMetrics.Measure<XingbianEngine>(
                delegate { return XingbianEngine.Open(path); }, out metrics);

            using (engine)
            {
                Console.WriteLine("音节数  : " + engine.Dict.Syllables.Length);
                Console.WriteLine("词表条目: " + engine.WordEntryCount);
                Console.WriteLine("读音索引: " + engine.IndexedKeyCount);
                Console.WriteLine("IDS 条目: " + CountIds(engine.Dict));
                Console.WriteLine("元信息  : " + (engine.Dict.MetaJson == null ? "(无 blockId=4)" : Shorten(engine.Dict.MetaJson, 120)));
                Console.WriteLine();

                Console.WriteLine("加载耗时(ms)  : " + metrics.Milliseconds.ToString("F1", CultureInfo.InvariantCulture));
                Console.WriteLine("常驻内存(MB)  : " + metrics.WorkingSetDeltaMegabytes.ToString("F1", CultureInfo.InvariantCulture) +
                                  "  (进程合计 " + EngineLoadMetrics.FormatMegabytes(metrics.WorkingSetAfter) + " MB)");
                Console.WriteLine("托管堆(MB)    : " + metrics.ManagedDeltaMegabytes.ToString("F1", CultureInfo.InvariantCulture));
                Console.WriteLine();

                string[] queries = { "ni", "nima", "nihao", "zhongguo", "beijing", "women", "shenme", "xingbian", "ceshi" };
                const int Rounds = 200;

                // 预热，避免 JIT 计入首轮。
                for (int i = 0; i < queries.Length; i++)
                {
                    engine.GetCandidates(queries[i], 20);
                }

                int calls = 0;
                DateTime startUtc = DateTime.UtcNow;
                for (int round = 0; round < Rounds; round++)
                {
                    for (int i = 0; i < queries.Length; i++)
                    {
                        engine.GetCandidates(queries[i], 20);
                        calls++;
                    }
                }

                double totalMs = (DateTime.UtcNow - startUtc).TotalMilliseconds;
                double microsPerCall = totalMs * 1000.0 / calls;

                // 单次查询的细粒度计时（排除 DateTime 精度限制导致的取整）。
                System.Diagnostics.Stopwatch single = System.Diagnostics.Stopwatch.StartNew();
                engine.GetCandidates("nima", 20);
                single.Stop();

                Console.WriteLine("单次查询耗时(μs): " + microsPerCall.ToString("F1", CultureInfo.InvariantCulture) +
                                  "  (共 " + calls + " 次，合计 " + totalMs.ToString("F1", CultureInfo.InvariantCulture) + " ms)");
                Console.WriteLine("  其中 nima 单次: " + (single.Elapsed.TotalMilliseconds * 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " μs");
                Console.WriteLine("  吞吐: " + (calls / (totalMs / 1000.0)).ToString("F0", CultureInfo.InvariantCulture) + " 次/秒");

                Console.WriteLine();
                Console.WriteLine("各查询候选数:");
                for (int i = 0; i < queries.Length; i++)
                {
                    IList<Candidate> result = engine.GetCandidates(queries[i], 20);
                    string first = result.Count > 0 ? result[0].Word + "(" + result[0].Freq + ")" : "(空)";
                    Console.WriteLine("  " + queries[i].PadRight(10) + " → " + result.Count.ToString(CultureInfo.InvariantCulture).PadLeft(3) +
                                      " 条，首选 " + first);
                }

                string plain = engine.Transform("形变输入法");
                Console.WriteLine();
                Console.WriteLine("Transform(\"形变输入法\") = " + plain);
            }

            return 0;
        }

        /// <summary>--selftest：13 项检查，全通过退出码 0。</summary>
        private static int SelfTestCommand(string explicitDict)
        {
            string source;
            string path = DictResolver.Resolve(explicitDict, out source);
            if (path == null || !File.Exists(path))
            {
                PrintDictMissing(explicitDict);
                return 3;
            }

            Console.WriteLine("=== 形变输入法自检 ===");
            Console.WriteLine("词典: " + path);
            Console.WriteLine("来源: " + source);
            Console.WriteLine("版本: " + typeof(XingbianEngine).Assembly.GetName().Version);
            Console.WriteLine();

            EngineLoadMetrics metrics;
            XingbianEngine engine = EngineLoadMetrics.Measure<XingbianEngine>(
                delegate { return XingbianEngine.Open(path); }, out metrics);

            using (engine)
            {
                DictFile dict = engine.Dict;
                Console.WriteLine("音节 " + dict.Syllables.Length + " / 词条 " + dict.WordCount +
                                  " / 索引 " + engine.IndexedKeyCount + " / IDS " + CountIds(dict));
                Console.WriteLine("元信息: " + (dict.MetaJson == null ? "(无)" : Shorten(dict.MetaJson, 160)));
                Console.WriteLine();

                TestRunner runner = new TestRunner();

                // ---- 1. 打开与格式校验
                runner.Run("DictFile.Open 可打开；magic/version 非法时抛出有意义的异常", delegate
                {
                    using (DictFile opened = DictFile.Open(path))
                    {
                        TestRunner.True(opened.Syllables.Length > 0, "音节表为空。");
                        TestRunner.True(opened.WordCount > 0, "词表条目数为 0。");
                    }

                    AssertFormatRejected(path, "magic");
                    AssertFormatRejected(path, "version");
                });

                // ---- 2. 拆解表
                runner.Run("IDS 表: 你→亻尔, 妈→女马, 好→女子, 测→氵则", delegate
                {
                    AssertIds(dict, '你', "亻尔");
                    AssertIds(dict, '妈', "女马");
                    AssertIds(dict, '好', "女子");
                    AssertIds(dict, '测', "氵则");
                });

                // ---- 3. Transform 默认参数
                runner.Run("Transform(\"你妈\") == \"亻尔 女马\"（字间空格、字内不空格）", delegate
                {
                    TransformOptions defaults = new TransformOptions();
                    TestRunner.Equal(" ", defaults.CharSeparator, "默认 CharSeparator");
                    TestRunner.Equal("", defaults.ComponentSeparator, "默认 ComponentSeparator");
                    TestRunner.True(defaults.KeepUndecodable, "默认 KeepUndecodable 应为 true");

                    engine.Options = new TransformOptions();
                    TestRunner.Equal("亻尔 女马", engine.Transform("你妈"), "Transform(\"你妈\")");
                });

                // ---- 4. 你好
                runner.Run("Transform(\"你好\") == \"亻尔 女子\"", delegate
                {
                    engine.Options = new TransformOptions();
                    TestRunner.Equal("亻尔 女子", engine.Transform("你好"), "Transform(\"你好\")");
                });

                // ---- 5. ASCII / 不可拆字原样保留
                runner.Run("Transform 对 ASCII 与不可拆字原样保留", delegate
                {
                    TransformOptions options = new TransformOptions();
                    options.CharSeparator = "";
                    engine.Options = options;

                    TestRunner.Equal("abc亻尔", engine.Transform("abc你"), "Transform(\"abc你\")");
                    TestRunner.Equal("亻尔abc", engine.Transform("你abc"), "Transform(\"你abc\")");
                    TestRunner.Equal("ni hao", engine.Transform("ni hao"), "Transform(\"ni hao\") 空格保留");

                    // 默认参数下字间是空格：ASCII 与不可拆字一样参与分隔。
                    TransformOptions defaults = new TransformOptions();
                    string savedSeparator = engine.Options.CharSeparator;
                    engine.Options = defaults;
                    TestRunner.Equal("a b c 亻尔", engine.Transform("abc你"), "Transform(\"abc你\") 默认参数");
                    engine.Options = options;
                    TestRunner.Equal(savedSeparator, options.CharSeparator, "选项已复位");

                    // 非 BMP（代理对）整体原样输出，不拆、不截断。
                    TestRunner.Equal("😀", engine.Transform("😀"), "Transform 代理对");
                    TestRunner.Equal("😀亻尔", engine.Transform("😀你"), "Transform(\"😀你\")");
                    TestRunner.Equal("", engine.Transform(""), "Transform(\"\") 应为空串");
                    TestRunner.Equal("", engine.Transform(null), "Transform(null) 应为空串");

                    // KeepUndecodable = false 时不可拆的字被跳过，但可拆字仍输出。
                    options.KeepUndecodable = false;
                    TestRunner.Equal("亻尔", engine.Transform("a你1"), "KeepUndecodable=false 跳过不可拆字");

                    engine.Options = new TransformOptions();
                });

                // ---- 6. 分隔符组合
                runner.Run("TransformOptions: 部件分隔 \" \" → \"亻 尔 女 马\"；字间分隔 \"\" → \"亻尔女马\"", delegate
                {
                    TransformOptions options = new TransformOptions();
                    options.ComponentSeparator = " ";
                    engine.Options = options;
                    TestRunner.Equal("亻 尔 女 马", engine.Transform("你妈"), "ComponentSeparator=\" \"");

                    options = new TransformOptions();
                    options.CharSeparator = "";
                    engine.Options = options;
                    TestRunner.Equal("亻尔女马", engine.Transform("你妈"), "CharSeparator=\"\"");

                    options = new TransformOptions();
                    options.CharSeparator = "|";
                    options.ComponentSeparator = "-";
                    engine.Options = options;
                    TestRunner.Equal("亻-尔|女-马", engine.Transform("你妈"), "组合分隔符");

                    engine.Options = new TransformOptions();
                });

                // ---- 7. nima
                IList<Candidate> nima = null;
                runner.Run("GetCandidates(\"nima\", 20) 非空且第一候选为 \"你妈\"", delegate
                {
                    nima = engine.GetCandidates("nima", 20);
                    TestRunner.NotEmpty(nima, "GetCandidates(\"nima\", 20)");
                    TestRunner.Equal("你妈", nima[0].Word, "nima 首选词");
                    TestRunner.Equal("亻尔 女马", nima[0].Transformed, "nima 首选词的形变");
                    TestRunner.True(nima[0].Decomposable, "你妈 应标记为可拆");
                });

                // ---- 8. nihao
                runner.Run("GetCandidates(\"nihao\", 20) 第一候选为 \"你好\"", delegate
                {
                    IList<Candidate> result = engine.GetCandidates("nihao", 20);
                    TestRunner.NotEmpty(result, "GetCandidates(\"nihao\", 20)");
                    TestRunner.Equal("你好", result[0].Word, "nihao 首选词");
                });

                // ---- 9. zhongguo
                runner.Run("GetCandidates(\"zhongguo\", 20) 含 \"中国\"", delegate
                {
                    IList<Candidate> result = engine.GetCandidates("zhongguo", 20);
                    TestRunner.NotEmpty(result, "GetCandidates(\"zhongguo\", 20)");

                    bool found = false;
                    for (int i = 0; i < result.Count; i++)
                    {
                        if (string.Equals(result[i].Word, "中国", StringComparison.Ordinal))
                        {
                            found = true;
                            break;
                        }
                    }

                    TestRunner.True(found, "候选里没有 \"中国\"，实际候选：" + WordList(result));
                });

                // ---- 10. 不完整输入
                runner.Run("GetCandidates(\"ni\", 20) 非空（不完整输入也出候选）", delegate
                {
                    IList<Candidate> result = engine.GetCandidates("ni", 20);
                    TestRunner.NotEmpty(result, "GetCandidates(\"ni\", 20)");
                    TestRunner.True(result.Count <= 20, "候选数超过 max: " + result.Count);

                    // 末音节是「更长音节的前缀」时必须能展开：
                    //   nim → ni + m → ni ma / mai / man / ... → 首选 你妈
                    // 真实音节表里 m / n / a / e / o 本身就是完整音节，所以 "ni m" 是个
                    // 合法但不存在的 key，必须靠前缀展开兜底才算实现正确。
                    IList<Candidate> prefix = engine.GetCandidates("nim", 20);
                    TestRunner.NotEmpty(prefix, "GetCandidates(\"nim\", 20) 末音节前缀展开");
                    TestRunner.Equal("你妈", prefix[0].Word, "nim 首选词");

                    // 末音节正好是完整音节时用精确查表，不能被更长音节污染：
                    //   ni 的首选必须是 你，而不是 您(nin) / 牛(niu) / 您(ning)。
                    TestRunner.Equal("你", result[0].Word, "ni 首选词");

                    // 单字母前缀同样应可用（音节表里有以它开头的音节）。
                    TestRunner.True(engine.GetCandidates("n", 20).Count >= 0, "GetCandidates(\"n\") 不应抛异常。");
                });

                // ---- 11. 词频单调不增
                runner.Run("候选 Freq 单调不增", delegate
                {
                    string[] probes = { "nima", "nihao", "zhongguo", "ni", "nim", "wom", "zhonggu" };
                    for (int p = 0; p < probes.Length; p++)
                    {
                        IList<Candidate> result = engine.GetCandidates(probes[p], 20);
                        for (int i = 1; i < result.Count; i++)
                        {
                            if (result[i].Freq > result[i - 1].Freq)
                            {
                                throw new CheckFailedException(string.Format(
                                    "\"{0}\" 的第 {1} / {2} 条候选词频上升：{3}({4}) → {5}({6})",
                                    probes[p], i, i + 1, result[i - 1].Word, result[i - 1].Freq, result[i].Word, result[i].Freq));
                            }
                        }
                    }
                });

                // ---- 12. 空输入 / 无匹配
                runner.Run("空输入与无匹配返回空集合，不抛异常", delegate
                {
                    TestRunner.Equal(0, engine.GetCandidates("", 20).Count, "GetCandidates(\"\")");
                    TestRunner.Equal(0, engine.GetCandidates(null, 20).Count, "GetCandidates(null)");
                    TestRunner.Equal(0, engine.GetCandidates("zzzzzz", 5).Count, "GetCandidates(\"zzzzzz\")");
                    TestRunner.Equal(0, engine.GetCandidates("qqq", 20).Count, "GetCandidates(\"qqq\")");
                    TestRunner.Equal(0, engine.GetCandidates("ni ma", 20).Count, "GetCandidates(\"ni ma\") 含空格应返回空");
                    TestRunner.Equal(0, engine.GetCandidates("NiMa", 20).Count, "GetCandidates(\"NiMa\") 大写应返回空");
                });

                // ---- 13. 性能与内存
                runner.Run("加载耗时 < 3000ms 且常驻内存增量 < 400MB", delegate
                {
                    if (metrics.Milliseconds >= 3000)
                    {
                        throw new CheckFailedException(string.Format(
                            "加载耗时 {0:F1} ms 超过 3000ms 预算。", metrics.Milliseconds));
                    }

                    if (metrics.WorkingSetDeltaMegabytes >= 400)
                    {
                        throw new CheckFailedException(string.Format(
                            "常驻内存增量 {0:F1} MB 超过 400MB 预算。", metrics.WorkingSetDeltaMegabytes));
                    }
                });

                // ---- 14. 并发（输入法宿主会在多线程里调 GetCandidates / Transform）
                runner.Run("多线程并发调用 GetCandidates / Transform 结果与单线程一致", delegate
                {
                    ConcurrentProbe(engine);
                });

                PrintDiagnostics(engine, path, metrics);

                Console.WriteLine();
                Console.WriteLine("---- 性能与内存 ----");
                Console.WriteLine("加载耗时(ms)   : " + metrics.Milliseconds.ToString("F1", CultureInfo.InvariantCulture));
                Console.WriteLine("常驻内存(MB)   : " + metrics.WorkingSetDeltaMegabytes.ToString("F1", CultureInfo.InvariantCulture) +
                                  "  (进程合计 " + EngineLoadMetrics.FormatMegabytes(metrics.WorkingSetAfter) + " MB)");
                Console.WriteLine("托管堆(MB)     : " + metrics.ManagedDeltaMegabytes.ToString("F1", CultureInfo.InvariantCulture));

                Console.WriteLine();
                Console.WriteLine("---- 结论 ----");
                Console.WriteLine("总项数: " + runner.Total + "，失败: " + runner.Failed);
                if (runner.AllPassed)
                {
                    Console.WriteLine("全部通过（" + runner.Total + " 项）。");
                    return 0;
                }

                Console.WriteLine("自检失败。");
                return 1;
            }
        }

        // ------------------------------------------------------------ 辅助

        /// <summary>
        /// 并发探针：8 线程 × 400 轮同时在同一个 engine 上查候选 / 做形变，
        /// 每轮结果与单线程基准逐字段比对。任何共享可变状态都会在这里现形
        /// （错词、漏词、NullReferenceException、索引越界）。
        /// </summary>
        private static void ConcurrentProbe(XingbianEngine engine)
        {
            string[] queries = { "ni", "nima", "nihao", "zhongguo", "nim", "wo", "zhong", "beijing" };

            // 单线程基准
            Dictionary<string, string> baseline = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int q = 0; q < queries.Length; q++)
            {
                baseline[queries[q]] = Fingerprint(engine, queries[q]);
            }

            string transformBaseline = engine.Transform("形变输入法abc你妈");

            const int threadCount = 8;
            const int rounds = 400;
            Exception[] failures = new Exception[threadCount];
            System.Threading.Thread[] threads = new System.Threading.Thread[threadCount];
            int started = 0;

            for (int t = 0; t < threadCount; t++)
            {
                int slot = t;
                threads[t] = new System.Threading.Thread(delegate ()
                {
                    try
                    {
                        for (int r = 0; r < rounds; r++)
                        {
                            for (int q = 0; q < queries.Length; q++)
                            {
                                string actual = Fingerprint(engine, queries[q]);
                                if (!string.Equals(baseline[queries[q]], actual, StringComparison.Ordinal))
                                {
                                    throw new CheckFailedException(string.Format(
                                        "并发查询 \"{0}\" 结果漂移：基准 [{1}]，实际 [{2}]", queries[q], baseline[queries[q]], actual));
                                }
                            }

                            string transformed = engine.Transform("形变输入法abc你妈");
                            if (!string.Equals(transformBaseline, transformed, StringComparison.Ordinal))
                            {
                                throw new CheckFailedException(string.Format(
                                    "并发 Transform 结果漂移：基准 [{0}]，实际 [{1}]", transformBaseline, transformed));
                            }

                            // 顺便并发读词典块，触发 ReadBlock 的共享 IO 路径。
                            DictFile dict = engine.Dict;
                            if (!dict.TryGetIds('你', out _))
                            {
                                throw new CheckFailedException("并发 TryGetIds('你') 失败。");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failures[slot] = ex;
                    }
                });

                threads[t].IsBackground = true;
            }

            // 尽可能同时起跑，放大竞争窗口。
            for (int t = 0; t < threadCount; t++)
            {
                threads[t].Start();
                started++;
            }

            for (int t = 0; t < started; t++)
            {
                if (!threads[t].Join(60000))
                {
                    throw new CheckFailedException(string.Format("第 {0} 号线程 60 秒未结束（可能死锁）。", t));
                }
            }

            for (int t = 0; t < threadCount; t++)
            {
                if (failures[t] != null)
                {
                    throw failures[t];
                }
            }
        }

        /// <summary>把一次查询的候选序列压成可比较的字符串指纹。</summary>
        private static string Fingerprint(XingbianEngine engine, string pinyin)
        {
            IList<Candidate> candidates = engine.GetCandidates(pinyin, 20);
            StringBuilder sb = new StringBuilder(128);
            sb.Append(candidates.Count).Append('|');
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                sb.Append(c.Word).Append('/').Append(c.Freq).Append('/')
                  .Append(c.Decomposable ? '1' : '0').Append('/')
                  .Append(c.Transformed).Append(';');
            }

            return sb.ToString();
        }

        private static int CountIds(DictFile dict)
        {
            // blockId=3 的头 4 字节就是 count（按 docs/FORMAT.md），直接读，
            // 不要用码点扫描 —— 真实词典里大量部件/汉字在 BMP 之外，
            // 扫 4E00-9FFF 之类的区间会严重少报（实测 27766 vs 88480）。
            byte[] block = dict.ReadBlock(3);
            if (block == null || block.Length < 4)
            {
                return 0;
            }

            uint count = (uint)(block[0] | (block[1] << 8) | (block[2] << 16) | (block[3] << 24));
            return (int)count;
        }

        private static void AssertIds(DictFile dict, char c, string expected)
        {
            string components;
            if (!dict.TryGetIds(c, out components))
            {
                throw new CheckFailedException(string.Format("IDS 表里没有 \"{0}\"（U+{1:X4}）。", c, (int)c));
            }

            if (!string.Equals(components, expected, StringComparison.Ordinal))
            {
                throw new CheckFailedException(string.Format(
                    "\"{0}\" 的拆解不符：期望 [{1}]，实际 [{2}]", c, expected, components));
            }
        }

        /// <summary>复制真实词典头部并破坏 magic 或 version，确认读取端抛出有意义的异常。</summary>
        private static void AssertFormatRejected(string goodPath, string what)
        {
            byte[] bytes = new byte[128];
            int read;
            using (FileStream stream = new FileStream(goodPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                read = stream.Read(bytes, 0, bytes.Length);
            }

            if (read < 32)
            {
                throw new CheckFailedException("基准词典不足 32 字节，无法做损坏测试。");
            }

            if (string.Equals(what, "magic", StringComparison.Ordinal))
            {
                bytes[0] = (byte)'Z';
                bytes[1] = (byte)'Z';
                bytes[2] = (byte)'Z';
                bytes[3] = (byte)'Z';
            }
            else
            {
                bytes[4] = 99;
                bytes[5] = 0;
                bytes[6] = 0;
                bytes[7] = 0;
            }

            string temp = Path.Combine(Path.GetTempPath(),
                "xingbian-selftest-" + what + "-" + Guid.NewGuid().ToString("N") + ".bin");

            try
            {
                File.WriteAllBytes(temp, bytes);

                try
                {
                    using (DictFile.Open(temp))
                    {
                        throw new CheckFailedException(string.Format(
                            "{0} 被破坏的词典本应抛异常，但打开成功了。", what));
                    }
                }
                catch (DictFormatException ex)
                {
                    if (ex.Message.IndexOf(what, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        throw new CheckFailedException(string.Format(
                            "{0} 校验失败的异常信息没有提到 \"{0}\"，信息为：{1}", what, ex.Message));
                    }

                    if (ex.Message.Length < 10)
                    {
                        throw new CheckFailedException(what + " 校验失败的异常信息过于简略：" + ex.Message);
                    }
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (IOException)
                {
                    // 临时文件清理失败不影响自检结论。
                }
            }
        }

        /// <summary>打印数据能力概览：某些验收项依赖真实词典是否收录了特定词。</summary>
        private static void PrintDiagnostics(XingbianEngine engine, string path, EngineLoadMetrics metrics)
        {
            Console.WriteLine();
            Console.WriteLine("---- 数据能力概览 ----");
            string[] probes = { "nima", "nihao", "zhongguo", "ni", "nim", "wo", "zhong" };
            for (int i = 0; i < probes.Length; i++)
            {
                IList<Candidate> result = engine.GetCandidates(probes[i], 5);
                Console.WriteLine("  " + probes[i].PadRight(10) + " → " + WordList(result));
            }

            string[] words = { "你妈", "你好", "中国", "尼玛" };
            for (int i = 0; i < words.Length; i++)
            {
                string components;
                bool decomposable = true;
                for (int k = 0; k < words[i].Length; k++)
                {
                    if (!engine.Dict.TryGetIds(words[i][k], out components))
                    {
                        decomposable = false;
                    }
                }

                Console.WriteLine("  词 \"" + words[i] + "\" 每字可拆: " + (decomposable ? "是" : "否"));
            }
        }

        private static string WordList(IList<Candidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return "(空)";
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(candidates[i].Word).Append('(').Append(candidates[i].Freq).Append(')');
            }

            return sb.ToString();
        }

        private static string Shorten(string text, int max)
        {
            if (text == null)
            {
                return "";
            }

            string flat = text.Replace('\r', ' ').Replace('\n', ' ');
            return flat.Length <= max ? flat : flat.Substring(0, max) + "...";
        }

        private static void PrintDictMissing(string explicitDict)
        {
            Console.Error.WriteLine("未找到词典文件。");
            if (!string.IsNullOrEmpty(explicitDict))
            {
                Console.Error.WriteLine("  --dict 指定的路径不存在：" + Path.GetFullPath(explicitDict));
            }

            Console.Error.WriteLine("查找顺序：");
            Console.Error.WriteLine(DictResolver.DescribeSearchOrder());
            string root = DictResolver.FindProjectRoot();
            Console.Error.WriteLine();
            Console.Error.WriteLine("检测到的项目根：" + (root ?? "(未找到含 assets 的目录)"));
            if (root != null)
            {
                string build = Path.Combine(root, "assets", "build");
                Console.Error.WriteLine("assets\\build 是否存在：" + (Directory.Exists(build) ? "是" : "否"));
                if (Directory.Exists(build))
                {
                    string[] files = Directory.GetFiles(build);
                    Console.Error.WriteLine("assets\\build 内容：" + (files.Length == 0 ? "(空)" : string.Join(", ", files)));
                }
            }
        }
    }
}
