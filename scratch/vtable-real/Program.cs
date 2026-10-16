// 对【真实交付物】Xingbian.Tsf.dll 做 CCW vtable 逐槽取证。
//
// 背景：C# 里 `interface Ex : Base` 这种接口继承形状，会让 CLR 为 Ex 生成的 CCW
// vtable 只写 4 个槽（0..3），把 ActivateEx 塞进槽 3，槽 4/5 留着 CLR 内部
// pre-code 字节。而 TSF 是先 QI 到 ITfTextInputProcessorEx、再调槽 5 的 ActivateEx，
// 于是"一选输入法就崩"。修法是把 Ex 扁平声明（不继承），实现类同时列出两个接口。
//
// 本程序验证的就是这个修法在**交付物本身**上生效，而不是在样机上生效。
//
// 用法：
//   RealVtable.exe introspect      打印槽位指针（只读，不调用）
//   RealVtable.exe call <b|e> <slot>   调一个槽并打印 hr（崩了就是结论）
//
// 期望（TextService 对 null ptim 的行为）：
//   槽 3 = Activate   -> ActivateEx(null,0,0) -> E_INVALIDARG 0x80070057
//   槽 4 = Deactivate -> Deactivate()          -> S_OK        0x00000000
//   槽 5 = ActivateEx -> ActivateEx(null,0,0)  -> E_INVALIDARG 0x80070057
// 修复前：槽 5 是 pre-code，直接 access violation。

using System;
using System.Runtime.InteropServices;
using Xingbian.Tsf;
using Xingbian.Tsf.Interop;

namespace RealVtableProbe
{
    internal static class Program
    {
        // 与被测方法参数个数一致（x64 下多传的参数落在易失寄存器里，无害）
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Fn4(IntPtr self, IntPtr a, uint b, uint c);

        private static readonly Guid IID_Base =
            new Guid("aa80e7f7-2021-11d2-93e0-0060b067b86e");
        private static readonly Guid IID_Ex =
            new Guid("6e4e2102-f9cd-433d-b496-303ce03a6507");

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (args.Length == 0)
            {
                Console.WriteLine("用法: RealVtable.exe introspect");
                Console.WriteLine("      RealVtable.exe call <b|e> <slot>");
                return 2;
            }

            // 用交付物自己的类型来建对象、取接口 —— 不给"我另抄了一份声明"留空间
            var svc = new TextService();

            object itf;
            if (args[0] == "introspect")
            {
                return Introspect(svc);
            }

            if (args[0] != "call" || args.Length < 3)
            {
                Console.WriteLine("参数不对。");
                return 2;
            }

            Type itfType = (args[1] == "b") ? typeof(ITfTextInputProcessor) : typeof(ITfTextInputProcessorEx);
            itf = GetInterface(svc, args[1]);
            if (itf == null) { Console.WriteLine("RESULT: QI_FAILED"); return 1; }

            int slot;
            if (!int.TryParse(args[2], out slot)) { Console.WriteLine("slot 不是数字"); return 2; }

            // 注意第二个参数必须是【接口类型】而不是 itf.GetType()（那会拿到 TextService 本体）
            IntPtr pIf = Marshal.GetComInterfaceForObject(itf, itfType);
            Console.WriteLine("iface=" + args[1] + " slot=" + slot + " pIf=0x" + pIf.ToInt64().ToString("X"));

            IntPtr vt = Marshal.ReadIntPtr(pIf);
            IntPtr addr = Marshal.ReadIntPtr(vt, slot * IntPtr.Size);
            Console.WriteLine("vtable=0x" + vt.ToInt64().ToString("X") +
                              " slotaddr=0x" + addr.ToInt64().ToString("X"));
            Console.Out.Flush();

            Fn4 fn = (Fn4)Marshal.GetDelegateForFunctionPointer(addr, typeof(Fn4));
            // 第一个参数必须是 this 指针（pIf），不是 0 —— 传 0 会解引用空指针
            int hr = fn(pIf, IntPtr.Zero, 0, 0);
            Console.WriteLine("RET hr=0x" + ((uint)hr).ToString("X8") + " -> " + Explain(hr, slot));
            return 0;
        }

        private static object GetInterface(TextService svc, string which)
        {
            if (which == "b") { return (ITfTextInputProcessor)svc; }
            return (ITfTextInputProcessorEx)svc;
        }

        private static int Introspect(TextService svc)
        {
            foreach (string which in new string[] { "b", "e" })
            {
                Type itfType = (which == "b") ? typeof(ITfTextInputProcessor) : typeof(ITfTextInputProcessorEx);
                object itf = GetInterface(svc, which);
                Guid want = (which == "b") ? IID_Base : IID_Ex;
                IntPtr pIf = Marshal.GetComInterfaceForObject(itf, itfType);
                IntPtr vt = Marshal.ReadIntPtr(pIf);

                Console.WriteLine("--- iface=" + which + " (" + itfType.Name + ") ---");
                Console.WriteLine("  pIf                 : 0x" + pIf.ToInt64().ToString("X"));
                Console.WriteLine("  declared [Guid]     : " + itfType.GUID +
                                  (itfType.GUID == want ? "   [MATCH expected]" : "   [MISMATCH!]"));

                // 再用 IID 直接 QI 一次，确认真实 COM 身份（这才是 TSF 走的那一步）
                IntPtr pQ;
                int hrQ = Marshal.QueryInterface(pIf, ref want, out pQ);
                Console.WriteLine("  QI(expected IID)    : hr=0x" + ((uint)hrQ).ToString("X8") +
                                  " ptr=0x" + pQ.ToInt64().ToString("X"));
                if (pQ != IntPtr.Zero) { Marshal.Release(pQ); }

                // 槽位指针全列出来
                for (int i = 0; i <= 5; i++)
                {
                    IntPtr a = Marshal.ReadIntPtr(vt, i * IntPtr.Size);
                    Console.WriteLine("  slot " + i + " = 0x" + a.ToInt64().ToString("X16"));
                }
                Console.WriteLine();
            }
            return 0;
        }

        private static string Explain(int hr, int slot)
        {
            uint u = (uint)hr;
            if (u == 0x80070057) { return "E_INVALIDARG (TextService 对 null ptim 的预期行为)"; }
            if (u == 0) { return "S_OK"; }
            if (u == 0x11111111) { return "Activate 哨兵"; }
            if (u == 0x22222222) { return "Deactivate 哨兵"; }
            if (u == 0x33333333) { return "ActivateEx 哨兵"; }
            return "(未知)";
        }
    }
}
