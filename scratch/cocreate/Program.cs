// CoCreate —— 用真实的 ole32!CoCreateInstance 走【真实 HKLM 注册表路径】
// 创建 Xingbian.Tsf.TextService，验证 TSF 运行时最关键的未知项：
//   「注册表那 6 个 InprocServer32 值，是否真的能让 COM 创建出这个托管类？」
//
// 为什么这条最要紧：
//   - scratch\lead-e2e 是 `new TextService()` 直接建对象，**绕过了 COM 激活**；
//   - scratch\regfree 测的是免注册（SxS 清单）路径；
//   - 而真实运行时里，msctf.dll 走的就是 CoCreateInstance(CLSID) -> 读 HKLM -> mscoree。
//   本程序补上的正是这一段。
//
// 局限（诚实声明）：CoCreateInstance 由托管进程用 P/Invoke 调用。它进的是真实的
//   ole32，走真实的注册表，但"调用者"本身是托管进程 —— 对注册表激活路径而言这个
//   差别不影响结论（激活靠的是 ole32 读 HKLM，与调用者是不是托管无关）。
//   它与"ctfmon 在记事本进程里加载"仍有距离，那段由 typetest 覆盖。

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CoCreateProbe
{
    internal static class Program
    {
        private const string Clsid = "7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43";
        private static readonly Guid IID_IUnknown =
            new Guid("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_ITfTextInputProcessor =
            new Guid("aa80e7f7-2021-11d2-93e0-0060b067b86e");
        private static readonly Guid IID_ITfTextInputProcessorEx =
            new Guid("6e4e2102-f9cd-433d-b496-303ce03a6507");
        private static readonly Guid IID_ITfKeyEventSink =
            new Guid("aa80e7f5-2021-11d2-93e0-0060b067b86e");
        private static readonly Guid IID_ITfThreadMgrEventSink =
            new Guid("aa80e80e-2021-11d2-93e0-0060b067b86e");

        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private const uint CLSCTX_LOCAL_SERVER = 0x4;

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern void CoUninitialize();

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter,
                                                   uint dwClsContext, ref Guid riid,
                                                   out IntPtr ppv);

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int CoGetClassObject(ref Guid rclsid, uint dwClsContext,
                                                   IntPtr pvReserved, ref Guid riid,
                                                   out IntPtr ppv);

        private static int _pass, _fail;

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { _pass++; Console.WriteLine("[ ok ] " + name + (detail.Length > 0 ? "  -- " + detail : "")); }
            else { _fail++; Console.WriteLine("[FAIL] " + name + (detail.Length > 0 ? "  -- " + detail : "")); }
        }

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }

            Console.WriteLine("CoCreate —— 真实 CoCreateInstance 走真实 HKLM 注册表路径");
            Console.WriteLine();

            int hrCo = CoInitializeEx(IntPtr.Zero, 2 /* APARTMENTTHREADED */);
            Console.WriteLine("CoInitializeEx hr=0x" + hrCo.ToString("X8"));
            Console.WriteLine();

            Guid clsid = new Guid(Clsid);

            // ---------- 1. CoGetClassObject：只要 InprocServer32 对了这里就该成功 ----------
            Console.WriteLine("--- 1. CoGetClassObject(CLSID, INPROC_SERVER) ---");
            Guid iidIClassFactory = new Guid("00000001-0000-0000-C000-000000000046");
            IntPtr pCf = IntPtr.Zero;
            int hr = CoGetClassObject(ref clsid, CLSCTX_INPROC_SERVER, IntPtr.Zero,
                                      ref iidIClassFactory, out pCf);
            Console.WriteLine("   hr=0x" + hr.ToString("X8") + "  pCf=0x" + pCf.ToInt64().ToString("X"));
            Check("CoGetClassObject 成功（说明 InprocServer32 -> mscoree.dll 生效）",
                  hr == 0 && pCf != IntPtr.Zero,
                  "hr=0x" + hr.ToString("X8"));
            if (pCf != IntPtr.Zero) { Marshal.Release(pCf); }
            Console.WriteLine();

            // ---------- 2. CoCreateInstance 到 IUnknown ----------
            Console.WriteLine("--- 2. CoCreateInstance(CLSID, IID_IUnknown) ---");
            IntPtr pUnk = IntPtr.Zero;
            Guid iidUnk = IID_IUnknown;
            hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER,
                                  ref iidUnk, out pUnk);
            Console.WriteLine("   hr=0x" + hr.ToString("X8") + "  pUnk=0x" + pUnk.ToInt64().ToString("X"));
            Check("CoCreateInstance 成功（CLR 真的把托管类建出来了）",
                  hr == 0 && pUnk != IntPtr.Zero, "hr=0x" + hr.ToString("X8"));
            if (hr != 0 || pUnk == IntPtr.Zero)
            {
                Console.WriteLine();
                Console.WriteLine("失败码解读：");
                Console.WriteLine("  0x80040154 REGDB_E_CLASSNOTREG —— CLSID 没注册 / InprocServer32 不对");
                Console.WriteLine("  0x80040111 CLASS_E_CLASSNOTAVAILABLE —— 类名不对，CLR 里找不到这个类");
                Console.WriteLine("  0x80131700 / 0x80131040 —— RuntimeVersion 或 Assembly 名不对");
                Console.WriteLine("  0x8007007E MOD_NOT_FOUND —— DLL 不存在 / CodeBase 指错");
                CoUninitialize();
                Console.WriteLine();
                Console.WriteLine("=== 通过 " + _pass + " 项，失败 " + _fail + " 项 ===");
                return _fail == 0 ? 0 : 1;
            }
            Console.WriteLine();

            // ---------- 3. 逐个 QI 到 TSF 接口（真实 vtable，不是反射） ----------
            Console.WriteLine("--- 3. 从原生 IUnknown 逐个 QI 到 TSF 接口 ---");
            QI(pUnk, IID_ITfTextInputProcessor, "ITfTextInputProcessor");
            QI(pUnk, IID_ITfTextInputProcessorEx, "ITfTextInputProcessorEx");
            QI(pUnk, IID_ITfKeyEventSink, "ITfKeyEventSink");
            QI(pUnk, IID_ITfThreadMgrEventSink, "ITfThreadMgrEventSink");
            Console.WriteLine();

            // ---------- 4. 真调一次槽 5（ActivateEx）看 vtable 对不对 ----------
            Console.WriteLine("--- 4. 真调 ITfTextInputProcessorEx 的槽 4/5 ---");
            IntPtr pEx = IntPtr.Zero;
            Guid iidEx = IID_ITfTextInputProcessorEx;
            hr = Marshal.QueryInterface(pUnk, ref iidEx, out pEx);
            if (hr == 0 && pEx != IntPtr.Zero)
            {
                // 槽 3 = Activate(ptim, tid)     -> 传 null ptim，期望 E_INVALIDARG
                // 槽 4 = Deactivate()            -> 期望 S_OK
                // 槽 5 = ActivateEx(ptim,tid,fl) -> 传 null ptim，期望 E_INVALIDARG
                // 注意：槽号要加上 IUnknown 的 3 个，所以实际索引 3/4/5
                int hr3 = CallSlot(pEx, 3, IntPtr.Zero, 0, 0);
                Console.WriteLine("   槽3 Activate(null,0)   hr=0x" + hr3.ToString("X8"));
                Check("槽3 落在 Activate 上（返回 E_INVALIDARG 而不是崩溃）",
                      hr3 == unchecked((int)0x80070057), "hr=0x" + hr3.ToString("X8"));

                int hr5 = CallSlot(pEx, 5, IntPtr.Zero, 0, 0);
                Console.WriteLine("   槽5 ActivateEx(null,0,0) hr=0x" + hr5.ToString("X8"));
                Check("槽5 落在 ActivateEx 上（这就是修复前必崩的那一槽）",
                      hr5 == unchecked((int)0x80070057), "hr=0x" + hr5.ToString("X8"));

                int hr4 = CallSlot(pEx, 4, IntPtr.Zero, 0, 0);
                Console.WriteLine("   槽4 Deactivate()       hr=0x" + hr4.ToString("X8"));
                Check("槽4 落在 Deactivate 上（返回 S_OK）",
                      hr4 == 0, "hr=0x" + hr4.ToString("X8"));

                Marshal.Release(pEx);
            }
            else
            {
                Check("QI 到 ITfTextInputProcessorEx", false, "hr=0x" + hr.ToString("X8"));
            }
            Console.WriteLine();

            Marshal.Release(pUnk);
            CoUninitialize();

            Console.WriteLine("=== 通过 " + _pass + " 项，失败 " + _fail + " 项 ===");
            Console.WriteLine();
            Console.WriteLine(_fail == 0
                ? "结论：注册表路径上的 COM 激活完全可用，且 ITfTextInputProcessorEx 的 vtable 形状正确。"
                : "结论：存在失败项，TSF 运行时会在这一步出问题。");
            return _fail == 0 ? 0 : 1;
        }

        private static void QI(IntPtr pUnk, Guid iid, string name)
        {
            IntPtr p = IntPtr.Zero;
            int hr = Marshal.QueryInterface(pUnk, ref iid, out p);
            Console.WriteLine("   " + name.PadRight(26) + " hr=0x" + hr.ToString("X8")
                              + "  ptr=0x" + p.ToInt64().ToString("X"));
            Check("QI 到 " + name, hr == 0 && p != IntPtr.Zero, "");
            if (p != IntPtr.Zero) { Marshal.Release(p); }
        }

        // 用函数指针直接调 vtable 槽，绕开任何托管封送层
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Fn3(IntPtr self, IntPtr a, uint b, uint c);

        private static int CallSlot(IntPtr pIf, int slot, IntPtr a, uint b, uint c)
        {
            IntPtr vtbl = Marshal.ReadIntPtr(pIf);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            Fn3 f = (Fn3)Marshal.GetDelegateForFunctionPointer(fn, typeof(Fn3));
            return f(pIf, a, b, c);
        }
    }
}
