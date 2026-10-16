// ProbeFix2 —— 逐槽独立进程调用版。
//
// 为什么改成这样：第一版在同一个进程里连调多个槽，碰到坏槽直接 access violation，
// 后面的形状根本没机会测。现在每次进程只调一个槽，崩了就崩了，退出码就是结论。
//
// 用法：
//   ProbeFix2.exe introspect                    打印三种形状的 vtable 槽位值（不调用）
//   ProbeFix2.exe call <shape> <iface> <slot>   只调一个槽并打印返回的指纹
//
// shape: A = 接口继承（现状）  B = 扁平  C = 基接口 + 扁平派生接口
// iface: b = ITfTextInputProcessor(5 槽)  e = ITfTextInputProcessorEx(6 槽)
// slot : 3/4/5 （3=Activate 4=Deactivate 5=ActivateEx 的期望绑定）
//
// 哨兵指纹：Activate -> 0x11111111 / Deactivate -> 0x22222222 / ActivateEx -> 0x33333333

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ProbeFix2
{
    internal static class Ids
    {
        public const string IID_Base = "aa80e7f7-2021-11d2-93e0-0060b067b86e";
        public const string IID_Ex = "6e4e2102-f9cd-433d-b496-303ce03a6507";
        public static readonly Guid GBase = new Guid(IID_Base);
        public static readonly Guid GEx = new Guid(IID_Ex);
    }

    // ---- 形状 A：C# 接口继承（现状 TextService 的写法）----
    [ComImport]
    [Guid(Ids.IID_Base)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface A_Base
    {
        [PreserveSig] int Activate(IntPtr ptim, uint tid);
        [PreserveSig] int Deactivate();
    }

    [ComImport]
    [Guid(Ids.IID_Ex)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface A_ExInherit : A_Base
    {
        [PreserveSig] int ActivateEx(IntPtr ptim, uint tid, uint dwFlags);
    }

    // ---- 形状 B/C：扁平声明（不继承，三个方法全部自己写）----
    [ComImport]
    [Guid(Ids.IID_Base)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface B_Base
    {
        [PreserveSig] int Activate(IntPtr ptim, uint tid);
        [PreserveSig] int Deactivate();
    }

    [ComImport]
    [Guid(Ids.IID_Ex)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface B_ExFlat
    {
        [PreserveSig] int Activate(IntPtr ptim, uint tid);
        [PreserveSig] int Deactivate();
        [PreserveSig] int ActivateEx(IntPtr ptim, uint tid, uint dwFlags);
    }

    // ---- 实现类 ----
    [ComVisible(true)]
    [Guid("BBBBBBBB-0001-4000-8000-000000000001")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ImplA : A_ExInherit
    {
        public int Activate(IntPtr ptim, uint tid) { return 0x11111111; }
        public int Deactivate() { return 0x22222222; }
        public int ActivateEx(IntPtr ptim, uint tid, uint dwFlags) { return 0x33333333; }
    }

    [ComVisible(true)]
    [Guid("BBBBBBBB-0002-4000-8000-000000000002")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ImplB : B_ExFlat
    {
        public int Activate(IntPtr ptim, uint tid) { return 0x11111111; }
        public int Deactivate() { return 0x22222222; }
        public int ActivateEx(IntPtr ptim, uint tid, uint dwFlags) { return 0x33333333; }
    }

    [ComVisible(true)]
    [Guid("BBBBBBBB-0003-4000-8000-000000000003")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ImplC : B_Base, B_ExFlat
    {
        public int Activate(IntPtr ptim, uint tid) { return 0x11111111; }
        public int Deactivate() { return 0x22222222; }
        public int ActivateEx(IntPtr ptim, uint tid, uint dwFlags) { return 0x33333333; }
    }

    internal static class Program
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Fn4(IntPtr self, IntPtr a, uint b, uint c);

        private static object Shape(string s)
        {
            switch (s.ToUpperInvariant())
            {
                case "A": return new ImplA();
                case "B": return new ImplB();
                case "C": return new ImplC();
                default: throw new ArgumentException("shape 必须是 A/B/C: " + s);
            }
        }

        private static Guid Iid(string i)
        {
            switch (i.ToLowerInvariant())
            {
                case "b": return Ids.GBase;
                case "e": return Ids.GEx;
                default: throw new ArgumentException("iface 必须是 b/e: " + i);
            }
        }

        /// <summary>按声明形状，该接口「应当」有多少个前 3 槽之外的方法槽。</summary>
        private static int DeclaredMethodCount(string shape, string iface)
        {
            bool isEx = iface.ToLowerInvariant() == "e";
            if (shape.ToUpperInvariant() == "A")
                return isEx ? 3 : 2;      // A_ExInherit 继承来 2 个 + 自己 1 个
            return isEx ? 3 : 2;           // B/C 扁平声明同样是 3 / 2
        }

        private static string Fingerprint(int hr)
        {
            switch (hr)
            {
                case 0x11111111: return "Activate";
                case 0x22222222: return "Deactivate";
                case 0x33333333: return "ActivateEx";
                case 0: return "(S_OK)";
                default: return "(其他)";
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }

            if (args.Length == 0)
            {
                Console.WriteLine("用法: ProbeFix2.exe introspect | call <A|B|C> <b|e> <slot>");
                return 2;
            }

            if (args[0] == "introspect")
            {
                string[] shapes = { "A", "B", "C" };
                string[] ifaces = { "b", "e" };
                foreach (string sh in shapes)
                {
                    foreach (string ifc in ifaces)
                    {
                        Console.WriteLine("shape=" + sh + " iface=" + ifc + " :");
                        DumpSlots(Shape(sh), Iid(ifc), DeclaredMethodCount(sh, ifc) + 3, sh, ifc);
                    }
                }
                return 0;
            }

            if (args[0] == "call")
            {
                if (args.Length < 4) { Console.WriteLine("需要 shape iface slot"); return 2; }
                return DoCall(args[1], args[2], int.Parse(args[3]));
            }

            Console.WriteLine("未知模式: " + args[0]);
            return 2;
        }

        private static void DumpSlots(object impl, Guid iid, int count, string sh, string ifc)
        {
            IntPtr pUnk = Marshal.GetIUnknownForObject(impl);
            IntPtr pIf = IntPtr.Zero;
            int hr = Marshal.QueryInterface(pUnk, ref iid, out pIf);
            Marshal.Release(pUnk);
            Console.WriteLine("   QI hr=0x" + hr.ToString("X8") + " ptr=0x" + pIf.ToInt64().ToString("X"));
            if (hr != 0 || pIf == IntPtr.Zero) { return; }

            IntPtr vt = Marshal.ReadIntPtr(pIf);
            Console.WriteLine("   vtable @ 0x" + vt.ToInt64().ToString("X"));

            // 期望槽数 = 3(IUnknown) + 声明的方法数；再往后多读 2 个用于观察邻接内容
            for (int i = 0; i < count + 2; i++)
            {
                IntPtr a = Marshal.ReadIntPtr(vt, i * IntPtr.Size);
                string nm;
                switch (i)
                {
                    case 0: nm = "QueryInterface"; break;
                    case 1: nm = "AddRef"; break;
                    case 2: nm = "Release"; break;
                    case 3: nm = "Activate"; break;
                    case 4: nm = "Deactivate"; break;
                    case 5: nm = "ActivateEx"; break;
                    default: nm = "越界[" + i + "]"; break;
                }
                string mark = (i >= count) ? "   <- 超出期望槽数" : "";
                Console.WriteLine("   [" + i + "] " + nm.PadRight(14) + " 0x" +
                                  a.ToInt64().ToString("X").PadLeft(16) + mark);
            }
            Marshal.Release(pIf);
        }

        private static int DoCall(string sh, string ifc, int slot)
        {
            object impl = Shape(sh);
            Guid iid = Iid(ifc);

            IntPtr pUnk = Marshal.GetIUnknownForObject(impl);
            IntPtr pIf = IntPtr.Zero;
            int hr = Marshal.QueryInterface(pUnk, ref iid, out pIf);
            Marshal.Release(pUnk);
            Console.WriteLine("shape=" + sh + " iface=" + ifc + " slot=" + slot +
                              " QI hr=0x" + hr.ToString("X8"));
            if (hr != 0 || pIf == IntPtr.Zero) { Console.WriteLine("RESULT: QI_FAILED"); return 1; }

            IntPtr vt = Marshal.ReadIntPtr(pIf);
            IntPtr addr = Marshal.ReadIntPtr(vt, slot * IntPtr.Size);
            Console.WriteLine("vtable=0x" + vt.ToInt64().ToString("X") +
                              " slotaddr=0x" + addr.ToInt64().ToString("X"));
            Console.Out.Flush();

            Fn4 fn = (Fn4)Marshal.GetDelegateForFunctionPointer(addr, typeof(Fn4));
            int r = fn(pIf, IntPtr.Zero, 0, 0);

            Console.WriteLine("RET hr=0x" + r.ToString("X8") + " -> " + Fingerprint(r));
            Marshal.Release(pIf);
            return 0;
        }
    }
}
