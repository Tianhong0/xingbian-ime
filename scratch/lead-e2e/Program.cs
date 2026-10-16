// LeadE2E —— 用假 TSF 上下文驱动真实 TextService 的状态机与提交路径。
// 详见 Fakes.cs 顶部说明。运行前设置 XINGBIAN_DICT 指向真实词典。

using System;
using System.Text;
using Xingbian.Tsf;
using Xingbian.Tsf.Interop;

namespace LeadE2E
{
    internal static class Program
    {
        private const int VK_BACK = 0x08;
        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_SHIFT = 0x10;
        private const int VK_1 = 0x31;
        private const int VK_2 = 0x32;

        private static int _pass;
        private static int _fail;

        [STAThread]
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }

            Console.WriteLine("LeadE2E —— 假 TSF 上下文 × 真实 TextService");
            Console.WriteLine("词典: " + (Environment.GetEnvironmentVariable("XINGBIAN_DICT") ?? "(未设置)"));

            // 先确认引擎能加载，否则所有用例都会退化成「无候选」，没有意义。
            string engineError;
            if (!TextService.Warmup(out engineError))
            {
                Console.WriteLine("[FAIL] 引擎预热失败: " + engineError);
                Console.WriteLine("       请设置 XINGBIAN_DICT 指向 assets\\build\\xingbian.bin");
                return 1;
            }
            Console.WriteLine("引擎预热成功。");
            Console.WriteLine();

            Case_MainChain();
            Case_LettersEaten();
            Case_DigitSelect();
            Case_EscapeThenSpace();
            Case_Backspace();
            Case_EmptyBufferPassThrough();
            Case_ShiftToggle();
            Case_NullContextSafe();
            Case_FallbackSetTextPath();
            Case_DeactivateUnadvises();
            Case_NullContextNotEaten();
            Case_ShiftLatch();

            Console.WriteLine();
            Console.WriteLine("=== 通过 " + _pass + " 项，失败 " + _fail + " 项 ===");
            return _fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // 测试脚手架
        // ------------------------------------------------------------------

        private sealed class Harness : IDisposable
        {
            public readonly FakeThreadMgr Thread = new FakeThreadMgr();
            public readonly FakeContext Context = new FakeContext();
            public TextService Service;

            public Harness()
            {
                Service = new TextService();
                int hr = Service.ActivateEx(Thread, 7, 0);
                if (hr != HResult.S_OK) { throw new InvalidOperationException("ActivateEx hr=" + hr); }
            }

            public bool Key(int vk)
            {
                bool eaten;
                // 与 TSF 的调用顺序一致：先 TestKeyDown 预检，被接受才走 KeyDown。
                Service.OnTestKeyDown(Context, Vk(vk), IntPtr.Zero, out eaten);
                if (!eaten) { return false; }
                bool eaten2;
                Service.OnKeyDown(Context, Vk(vk), IntPtr.Zero, out eaten2);
                return eaten2;
            }

            /// <summary>只做预检，不改状态（用于断言「没被吞」）。</summary>
            public bool TestOnly(int vk)
            {
                bool eaten;
                Service.OnTestKeyDown(Context, Vk(vk), IntPtr.Zero, out eaten);
                return eaten;
            }

            public void Type(string letters)
            {
                for (int i = 0; i < letters.Length; i++)
                {
                    Key(char.ToUpperInvariant(letters[i]) - 'A' + 0x41);
                }
            }

            public string Doc { get { return Context.Document.ToString(); } }

            public void Dispose()
            {
                try { Service.Deactivate(); } catch (Exception) { }
            }
        }

        private static IntPtr Vk(int vk)
        {
            return new IntPtr(vk);
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("[ ok ] " + name + (detail == null ? "" : " -- " + detail)); }
            else { _fail++; Console.WriteLine("[FAIL] " + name + " -- " + detail); }
        }

        private static void Info(string s)
        {
            Console.WriteLine("       " + s);
        }

        // ------------------------------------------------------------------
        // 用例
        // ------------------------------------------------------------------

        /// <summary>主链路：n i m a + 空格 → 文档里出现「亻尔 女马」。</summary>
        private static void Case_MainChain()
        {
            Console.WriteLine("--- 1. 主链路 nima + 空格 ---");
            using (Harness h = new Harness())
            {
                h.Type("nima");
                Info("输入 nima 后文档 = \"" + h.Doc + "\"（应为空，拼音只在浮窗里）");
                bool docEmptyDuringTyping = h.Doc.Length == 0;

                bool eaten = h.Key(VK_SPACE);
                string doc = h.Doc;
                Info("按空格后文档 = \"" + doc + "\" (空格被吞=" + eaten + ")");

                Check("输入过程中不写文档", docEmptyDuringTyping, "文档长度=" + h.Doc.Length);
                Check("nima+空格 上屏 = 亻尔 女马", doc == "亻尔 女马", "实际=\"" + doc + "\"");
                Check("空格被吞（不会插入空格）", eaten, "eaten=" + eaten);
                Check("走的是一次编辑会话", h.Context.EditSessionCount >= 1, "次数=" + h.Context.EditSessionCount);
            }
            Console.WriteLine();
        }

        /// <summary>四个字母都必须被吞，否则宿主会同时收到它们。</summary>
        private static void Case_LettersEaten()
        {
            Console.WriteLine("--- 2. 字母键必须被吞 ---");
            using (Harness h = new Harness())
            {
                int eatenCount = 0;
                foreach (char c in "nima")
                {
                    if (h.TestOnly(char.ToUpperInvariant(c) - 'A' + 0x41)) { eatenCount++; }
                }
                Check("n/i/m/a 四个字母全部被吞", eatenCount == 4, "eaten=" + eatenCount + "/4");
                Check("字母没被写进文档", h.Doc.Length == 0, "文档=\"" + h.Doc + "\"");
            }
            Console.WriteLine();
        }

        /// <summary>数字键直接选候选，且不同数字选到不同结果。</summary>
        private static void Case_DigitSelect()
        {
            Console.WriteLine("--- 3. 数字键选候选 ---");
            using (Harness h = new Harness())
            {
                h.Type("nima");
                bool eaten = h.Key(VK_1);
                string doc1 = h.Doc;
                Info("nima + 1 → \"" + doc1 + "\" (被吞=" + eaten + ")");
                Check("nima+1 上屏 = 亻尔 女马", doc1 == "亻尔 女马", "实际=\"" + doc1 + "\"");
                Check("数字键被吞", eaten, "eaten=" + eaten);
            }

            using (Harness h = new Harness())
            {
                h.Type("nima");
                h.Key(VK_2);
                string doc2 = h.Doc;
                Info("nima + 2 → \"" + doc2 + "\"");
                Check("nima+2 与 nima+1 结果不同", doc2 != "亻尔 女马" && doc2.Length > 0, "实际=\"" + doc2 + "\"");
            }
            Console.WriteLine();
        }

        /// <summary>Esc 取消后缓冲清空，接着的空格必须是宿主的正常空格。</summary>
        private static void Case_EscapeThenSpace()
        {
            Console.WriteLine("--- 4. Esc 取消 ---");
            using (Harness h = new Harness())
            {
                h.Type("nima");
                h.Key(VK_ESCAPE);
                bool spaceEaten = h.TestOnly(VK_SPACE);
                Info("Esc 后空格被吞 = " + spaceEaten);
                Check("Esc 清空缓冲（空格不再被吞）", !spaceEaten, "eaten=" + spaceEaten);
                Check("Esc 后文档仍为空", h.Doc.Length == 0, "文档=\"" + h.Doc + "\"");
            }
            Console.WriteLine();
        }

        /// <summary>退格删拼音字母，剩下的部分仍能查候选。</summary>
        private static void Case_Backspace()
        {
            Console.WriteLine("--- 5. 退格 ---");
            using (Harness h = new Harness())
            {
                h.Type("nima");
                h.Key(VK_BACK);
                h.Key(VK_BACK);
                bool eaten = h.Key(VK_SPACE);
                string doc = h.Doc;
                Info("nima + 退格×2 + 空格 → \"" + doc + "\"");
                Check("退格×2 后上屏 = 亻尔", doc == "亻尔", "实际=\"" + doc + "\"");
                Check("退格后的空格仍被吞", eaten, "eaten=" + eaten);
            }
            Console.WriteLine();
        }

        /// <summary>缓冲为空时，功能键必须完全放行，否则宿主连空格都打不出来。</summary>
        private static void Case_EmptyBufferPassThrough()
        {
            Console.WriteLine("--- 6. 空缓冲放行 ---");
            using (Harness h = new Harness())
            {
                bool b1 = h.TestOnly(VK_SPACE);
                bool b2 = h.TestOnly(VK_RETURN);
                bool b3 = h.TestOnly(VK_BACK);
                Info("空格=" + b1 + " 回车=" + b2 + " 退格=" + b3);
                Check("空缓冲时空格/回车/退格都不被吞", !b1 && !b2 && !b3,
                      "space=" + b1 + " enter=" + b2 + " back=" + b3);
            }
            Console.WriteLine();
        }

        /// <summary>单击 Shift 切中/英；英文态下字母不被吞。</summary>
        private static void Case_ShiftToggle()
        {
            Console.WriteLine("--- 7. Shift 切中英 ---");
            using (Harness h = new Harness())
            {
                bool before = h.TestOnly(0x4E);   // 'n'

                // 单击 Shift：TestKeyUp 记录 pending，KeyUp 执行切换
                bool upEaten;
                h.Service.OnTestKeyUp(h.Context, Vk(VK_SHIFT), IntPtr.Zero, out upEaten);
                h.Service.OnKeyUp(h.Context, Vk(VK_SHIFT), IntPtr.Zero, out upEaten);

                bool after = h.TestOnly(0x4E);
                Info("Shift 前字母被吞=" + before + "，Shift 后字母被吞=" + after);
                Check("切换前字母被吞", before, "eaten=" + before);
                Check("单击 Shift 后字母不再被吞（英文态）", !after, "eaten=" + after);
                Check("切换 Shift 本身不被吞", !upEaten, "eaten=" + upEaten);
            }
            Console.WriteLine();
        }

        /// <summary>pic = null 不能抛，必须返回 S_OK（TSF 在某些时机会传 null）。</summary>
        private static void Case_NullContextSafe()
        {
            Console.WriteLine("--- 8. pic=null 安全 ---");
            using (Harness h = new Harness())
            {
                bool eaten;
                Guid guid = Guid.Empty;
                int r1 = h.Service.OnKeyDown(null, Vk(0x4E), IntPtr.Zero, out eaten);
                int r2 = h.Service.OnKeyDown(null, Vk(VK_SPACE), IntPtr.Zero, out eaten);
                int r3 = h.Service.OnTestKeyDown(null, Vk(0x4E), IntPtr.Zero, out eaten);
                int r4 = h.Service.OnPreservedKey(null, ref guid, out eaten);
                Info("hr: OnKeyDown=" + r1 + " OnKeyDown(space)=" + r2 + " OnTestKeyDown=" + r3 + " OnPreservedKey=" + r4);
                Check("pic=null 时全部返回 S_OK 且不抛",
                      r1 == HResult.S_OK && r2 == HResult.S_OK && r3 == HResult.S_OK && r4 == HResult.S_OK,
                      r1 + "/" + r2 + "/" + r3 + "/" + r4);
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 逼出 SetText 后备路径：让 InsertTextAtSelection 故意失败。
        /// 这条路径平时走不到，但它手写了 TF_SELECTION 的 x64 内存布局，必须验证。
        /// </summary>
        private static void Case_FallbackSetTextPath()
        {
            Console.WriteLine("--- 9. SetText 后备路径（手工 TF_SELECTION 布局） ---");
            using (Harness h = new Harness())
            {
                h.Context.BreakPrimaryPath = true;   // 主路径返回 E_NOTIMPL

                h.Type("nima");
                bool eaten = h.Key(VK_SPACE);

                string docViaInsert = h.Doc;
                string docViaSetText = h.Context.Range.Written;

                Info("InsertTextAtSelection 文档 = \"" + docViaInsert + "\"");
                Info("SetText 写到 range 的内容 = \"" + docViaSetText + "\"（SetText 调用次数=" + h.Context.Range.SetTextCount + "）");

                Check("主路径失败后回落到 SetText 路径", h.Context.Range.SetTextCount == 1,
                      "SetTextCount=" + h.Context.Range.SetTextCount);
                Check("SetText 内容 = 亻尔 女马", docViaSetText == "亻尔 女马", "实际=\"" + docViaSetText + "\"");
                Check("主路径确实没写文档", docViaInsert.Length == 0, "文档=\"" + docViaInsert + "\"");
                Check("按键仍被吞", eaten, "eaten=" + eaten);
            }
            Console.WriteLine();
        }

        /// <summary>Deactivate 必须把两个 sink 都注销掉。</summary>
        private static void Case_DeactivateUnadvises()
        {
            Console.WriteLine("--- 10. Deactivate 注销回调 ---");
            Harness h = new Harness();
            bool advisedBefore = h.Thread.AdvisedKeySink && h.Thread.AdvisedThreadMgrSink;
            Info("激活后 AdviseKeyEventSink=" + h.Thread.AdvisedKeySink + " AdviseSink=" + h.Thread.AdvisedThreadMgrSink);

            h.Service.Deactivate();
            bool stillAdvised = h.Thread.AdvisedKeySink;

            Check("激活时两个 sink 都挂上了", advisedBefore,
                  "key=" + h.Thread.AdvisedKeySink + " threadmgr=" + h.Thread.AdvisedThreadMgrSink);
            Check("Deactivate 注销了键盘 sink", !stillAdvised, "AdvisedKeySink=" + h.Thread.AdvisedKeySink);

            // Deactivate 后必须停止吞键，否则输入法没了还在吃按键。
            bool eaten;
            h.Service.OnTestKeyDown(h.Context, Vk(0x4E), IntPtr.Zero, out eaten);
            Check("Deactivate 后不再吞键", !eaten, "eaten=" + eaten);
            Console.WriteLine();
        }

        /// <summary>
        /// 回归用例（曾为真实缺陷）：pic == null 时不能吞键。
        /// 旧实现 OnTestKeyDown 只看 ShouldEat，pic 为 null 也报「要吃」，
        /// 结果 OnKeyDown 插不进去，用户的按键被静默丢弃。
        /// </summary>
        private static void Case_NullContextNotEaten()
        {
            Console.WriteLine("--- 11. pic=null 不吞键（回归） ---");
            using (Harness h = new Harness())
            {
                bool eaten;
                int hr1 = h.Service.OnTestKeyDown(null, Vk(0x4E), IntPtr.Zero, out eaten);
                bool eatenKey = eaten;
                int hr2 = h.Service.OnTestKeyDown(null, Vk(VK_SPACE), IntPtr.Zero, out eaten);
                bool eatenSpace = eaten;
                Info("TestKeyDown(null,'n') hr=" + hr1 + " eaten=" + eatenKey
                     + "；TestKeyDown(null,space) hr=" + hr2 + " eaten=" + eatenSpace);
                Check("pic=null 时字母不被吞", !eatenKey, "eaten=" + eatenKey);
                Check("pic=null 时空格不被吞", !eatenSpace, "eaten=" + eatenSpace);
                Check("两个调用都返回 S_OK", hr1 == HResult.S_OK && hr2 == HResult.S_OK, hr1 + "/" + hr2);
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 回归用例（曾为真实缺陷）：退格清空缓冲后，第一次单击 Shift 失效。
        /// 旧实现任何被吞的非字母键都会把 _shiftChord 置真且不清理，
        /// 于是下一次 Shift 单击被判成「组合键」而不切中英。
        /// </summary>
        private static void Case_ShiftLatch()
        {
            Console.WriteLine("--- 12. Shift 闩锁回归（退格后单击 Shift 仍有效） ---");
            using (Harness h = new Harness())
            {
                h.Type("nima");
                h.Key(VK_BACK);   // 退格：旧实现会把 _shiftChord 置真并残留
                Info("已输入 nima 并退格一次（缓冲剩 nim）");

                bool upEaten;
                h.Service.OnTestKeyUp(h.Context, Vk(VK_SHIFT), IntPtr.Zero, out upEaten);
                h.Service.OnKeyUp(h.Context, Vk(VK_SHIFT), IntPtr.Zero, out upEaten);

                bool after = h.TestOnly(0x4E);   // 'n'
                Info("Shift 单击后字母被吞=" + after + "（False=已切英文态，即切换生效）");
                Check("退格后第一次单击 Shift 就能切中英", !after, "eaten=" + after);
            }
            Console.WriteLine();
        }
    }
}
