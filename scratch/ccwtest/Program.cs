using System;
using System.Runtime.InteropServices;

// ---- 实验 1：带 [ComImport] 的接口能否被托管类实现？ ----------------------------------
[ComImport]
[Guid("aa80e7f5-2021-11d2-93e0-0060b067b86e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IImported
{
    [PreserveSig] int Bar(int x);
    [PreserveSig] int Baz(IntPtr p);
}

[ComVisible(true)]
[Guid("11111111-2222-3333-4444-555555555555")]
[ClassInterface(ClassInterfaceType.None)]
public class ImportedImpl : IImported
{
    public int Bar(int x) { return x + 1; }
    public int Baz(IntPtr p) { return 7; }
}

// ---- 实验 2：不带 [ComImport]（纯托管、但带 Guid + InterfaceIsIUnknown）能否建 CCW？ ----
[Guid("aa80e7f5-2021-11d2-93e0-0060b067b86e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPlain
{
    [PreserveSig] int Bar(int x);
    [PreserveSig] int Baz(IntPtr p);
}

[ComVisible(true)]
[Guid("22222222-3333-4444-5555-666666666666")]
[ClassInterface(ClassInterfaceType.None)]
public class PlainImpl : IPlain
{
    public int Bar(int x) { return x + 1; }
    public int Baz(IntPtr p) { return 9; }
}

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int failures = 0;

        // --- 实验 1
        try
        {
            ImportedImpl f = new ImportedImpl();
            IImported i = f;                       // 关键：能否把实现类转成 ComImport 接口
            int r = i.Bar(41);
            IntPtr p = Marshal.GetComInterfaceForObject(f, typeof(IImported));
            Console.WriteLine("[1] ComImport impl: cast+call OK (" + r + "), CCW=0x" + p.ToString("X"));
            Marshal.Release(p);
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("[1] ComImport impl: FAILED -> " + ex.GetType().FullName + ": " + ex.Message);
        }

        // --- 实验 2
        try
        {
            PlainImpl f = new PlainImpl();
            IPlain i = f;
            int r = i.Bar(41);
            IntPtr p = Marshal.GetComInterfaceForObject(f, typeof(IPlain));
            Console.WriteLine("[2] plain impl:   cast+call OK (" + r + "), CCW=0x" + p.ToString("X"));

            // 关键：CCW 建出来的接口指针，QI 回去应该还能拿到 IPlain
            object back = Marshal.GetObjectForIUnknown(p);
            IPlain again = (IPlain)back;
            Console.WriteLine("[2] roundtrip QI OK -> " + again.Bar(100));
            Marshal.Release(p);
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("[2] plain impl:   FAILED -> " + ex.GetType().FullName + ": " + ex.Message);
        }

        // --- 实验 3：把 CCW 当 IUnknown 传给原生侧（模拟 AdviseKeyEventSink 的 object 参数）
        try
        {
            PlainImpl f = new PlainImpl();
            object boxed = f;
            IntPtr unk = Marshal.GetIUnknownForObject(boxed);
            IntPtr want = IntPtr.Zero;
            Guid iid = new Guid("aa80e7f5-2021-11d2-93e0-0060b067b86e");
            int hr = Marshal.QueryInterface(unk, ref iid, out want);
            Console.WriteLine("[3] QI(IID of IPlain) hr=0x" + hr.ToString("X8") + " ptr=0x" + want.ToString("X")
                              + (hr == 0 ? "  <= 原生侧能 QI 到我们的回调接口" : "  <= 原生侧 QI 失败！"));
            if (want != IntPtr.Zero) Marshal.Release(want);
            Marshal.Release(unk);
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("[3] QI test: FAILED -> " + ex.GetType().FullName + ": " + ex.Message);
        }

        // --- 实验 4（最关键）：走 [MarshalAs(UnmanagedType.Interface)] 完全相同的那条封送路径。
        //     RequestEditSession(…, ITfEditSession pes, …) 就是这么传的：运行时会
        //    GetComInterfaceForObject(obj, typeof(IImported)) 再 QI 出 IID_IImported 交给原生侧。
        //    如果 CCW 上暴露不出这个 IID，RequestEditSession 会拿到 E_NOINTERFACE，
        //    整个"编辑会话提交文本"的主路径就死了。
        try
        {
            ImportedImpl f = new ImportedImpl();
            IntPtr ccw = Marshal.GetComInterfaceForObject(f, typeof(IImported));
            Guid iid = typeof(IImported).GUID;
            IntPtr got = IntPtr.Zero;
            int hr = Marshal.QueryInterface(ccw, ref iid, out got);
            Console.WriteLine("[4] ComImport IID on CCW: hr=0x" + hr.ToString("X8")
                              + " iid=" + iid.ToString()
                              + (hr == 0 && got != IntPtr.Zero ? "  <= 原生侧能拿到回调接口，主路径成立" : "  <= 致命：CCW 未暴露该 IID"));
            if (hr != 0 || got == IntPtr.Zero) failures++;
            if (got != IntPtr.Zero) Marshal.Release(got);
            Marshal.Release(ccw);
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("[4] ComImport IID on CCW: FAILED -> " + ex.GetType().FullName + ": " + ex.Message);
        }

        Console.WriteLine(failures == 0 ? "RESULT: ALL GREEN" : "RESULT: " + failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}
