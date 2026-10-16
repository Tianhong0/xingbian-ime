// ccw_layout.cs -- CONTROL EXPERIMENT: how does the .NET CLR lay out the CCW vtable
// of an interface that DERIVES from another COM interface?
//
// This mirrors the TSF shape exactly:
//     ITfTextInputProcessor    : Activate(3 args), Deactivate(1 arg)
//     ITfTextInputProcessorEx  : ITfTextInputProcessor + ActivateEx(4 args)
//
// Two managed classes implement the same derived interface in the two ways a
// developer might write it:
//
//   ShapeInherit   class MiniA : IMiniDerived             (declare ONLY the new method)
//   ShapeRedeclare class MiniB : IMiniDerived2            (re-declare ALL three methods)
//
// Each method returns a distinctive code so a slot call TELLS us which method is
// wired to that slot:
//     A1 -> 0x1111    A2 -> 0x2222    A3 -> 0x80070057 when arg0 == NULL (else 0x3333)
//
// We then call slots 3/4/5 of each derived interface pointer. Correct COM layout is
// slot3 = A1, slot4 = A2, slot5 = A3. A crash or a wrong code is the finding.
//
// Build (no SDK needed -- in-box compiler):
//   & "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo \
//        /target:exe /out:ccw_layout.exe ccw_layout.cs
//
// Usage:  ccw_layout.exe inherit | redeclare | all
// (One shape per process, because a faulting slot kills the process.)

using System;
using System.Runtime.InteropServices;

// ---------------------------------------------------------------------------
// Shape 1: derived interface declares only its OWN method (what TsfInterop.cs does)
// ---------------------------------------------------------------------------
[ComImport, Guid("11111111-1111-1111-1111-111111111111"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMiniBase
{
    [PreserveSig] int A1(IntPtr p, uint tid);
    [PreserveSig] int A2();
}

[ComImport, Guid("22222222-2222-2222-2222-222222222222"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMiniDerived : IMiniBase
{
    [PreserveSig] int A3(IntPtr p, uint tid, uint flags);
}

[ComVisible(true)]
[Guid("33333333-3333-3333-3333-333333333333")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class MiniA : IMiniDerived
{
    public int A1(IntPtr p, uint tid) { return 0x1111; }
    public int A2() { return 0x2222; }
    public int A3(IntPtr p, uint tid, uint flags)
    {
        return p == IntPtr.Zero ? unchecked((int)0x80070057) : 0x3333;
    }
}

// ---------------------------------------------------------------------------
// Shape 2: derived interface RE-DECLARES all inherited methods as well
// ---------------------------------------------------------------------------
[ComImport, Guid("44444444-4444-4444-4444-444444444444"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMiniDerived2
{
    [PreserveSig] int A1(IntPtr p, uint tid);
    [PreserveSig] int A2();
    [PreserveSig] int A3(IntPtr p, uint tid, uint flags);
}

[ComVisible(true)]
[Guid("55555555-5555-5555-5555-555555555555")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class MiniB : IMiniDerived2
{
    public int A1(IntPtr p, uint tid) { return 0x1111; }
    public int A2() { return 0x2222; }
    public int A3(IntPtr p, uint tid, uint flags)
    {
        return p == IntPtr.Zero ? unchecked((int)0x80070057) : 0x3333;
    }
}

public static class Program
{
    static readonly Guid IID_Base = new Guid("11111111-1111-1111-1111-111111111111");
    static readonly Guid IID_Derived = new Guid("22222222-2222-2222-2222-222222222222");
    static readonly Guid IID_Derived2 = new Guid("44444444-4444-4444-4444-444444444444");

    const long TERMINATOR_HINT = 0;

    delegate int Fn1(IntPtr self);
    delegate int Fn2(IntPtr self, IntPtr a);
    delegate int Fn3(IntPtr self, IntPtr a, uint b);
    delegate int Fn4(IntPtr self, IntPtr a, uint b, uint c);

    static IntPtr VtblOf(IntPtr p)
    {
        // At address p sits the vtable POINTER; the method table starts there.
        return Marshal.ReadIntPtr(p);
    }

    static IntPtr Slot(IntPtr p, int i)
    {
        IntPtr vt = VtblOf(p);
        return Marshal.ReadIntPtr(vt, i * IntPtr.Size);
    }

    static void Main(string[] args)
    {
        string which = args.Length > 0 ? args[0] : "all";
        Console.WriteLine("ccw_layout.cs -- CLR CCW vtable layout for a DERIVED interface");
        Console.WriteLine("==============================================================");
        Console.WriteLine("Correct COM layout for the derived pointer is:");
        Console.WriteLine("  slot[3] = A1 -> 0x1111,  slot[4] = A2 -> 0x2222,  slot[5] = A3 -> 0x80070057");
        Console.WriteLine("");

        if (which == "inherit" || which == "all") RunInherit();
        if (which == "redeclare" || which == "all") RunRedeclare();
        Console.WriteLine("");
        Console.WriteLine("0x1111=A1  0x2222=A2  0x80070057=A3(null)  0x3333=A3(non-null)");
    }

    static void RunInherit()
    {
        Console.WriteLine("==============================================================");
        Console.WriteLine("SHAPE 1 (inherit): class MiniA : IMiniDerived  -- IMiniDerived declares ONLY A3");
        Console.WriteLine("==============================================================");
        MiniA obj = new MiniA();
        IntPtr unk = Marshal.GetIUnknownForObject(obj);
        IntPtr pBase = IntPtr.Zero, pDer = IntPtr.Zero;
        Guid iidB = IID_Base;      // local copies: the in-box C# 5 compiler
        Guid iidD = IID_Derived;   // rejects readonly statics as ref/out args
        int hrB = Marshal.QueryInterface(unk, ref iidB, out pBase);
        int hrD = Marshal.QueryInterface(unk, ref iidD, out pDer);
        Console.WriteLine("QI IMiniBase    hr=0x" + hrB.ToString("X8") + " ptr=0x" + pBase.ToInt64().ToString("X"));
        Console.WriteLine("QI IMiniDerived hr=0x" + hrD.ToString("X8") + " ptr=0x" + pDer.ToInt64().ToString("X"));
        Dump("IMiniBase", pBase, 5);
        Dump("IMiniDerived", pDer, 7);
        Console.WriteLine("calling IMiniDerived slots 3,4,5,6 (this, NULL, 0, 0):");
        CallSlot(pDer, 3);
        CallSlot(pDer, 4);
        CallSlot(pDer, 5);
        CallSlot(pDer, 6);
    }

    static void RunRedeclare()
    {
        Console.WriteLine("");
        Console.WriteLine("==============================================================");
        Console.WriteLine("SHAPE 2 (redeclare): class MiniB : IMiniDerived2 -- all three declared");
        Console.WriteLine("==============================================================");
        MiniB obj = new MiniB();
        IntPtr unk = Marshal.GetIUnknownForObject(obj);
        IntPtr pDer = IntPtr.Zero;
        Guid iidD2 = IID_Derived2;
        int hrD = Marshal.QueryInterface(unk, ref iidD2, out pDer);
        Console.WriteLine("QI IMiniDerived2 hr=0x" + hrD.ToString("X8") + " ptr=0x" + pDer.ToInt64().ToString("X"));
        Dump("IMiniDerived2", pDer, 7);
        Console.WriteLine("calling IMiniDerived2 slots 3,4,5,6 (this, NULL, 0, 0):");
        CallSlot(pDer, 3);
        CallSlot(pDer, 4);
        CallSlot(pDer, 5);
        CallSlot(pDer, 6);
    }

    static void Dump(string label, IntPtr p, int n)
    {
        if (p == IntPtr.Zero) { Console.WriteLine(label + ": NULL pointer"); return; }
        IntPtr vt = Marshal.ReadIntPtr(p);
        Console.WriteLine(label + "  this=0x" + p.ToInt64().ToString("X")
                          + "  vtbl=0x" + vt.ToInt64().ToString("X"));
        for (int i = 0; i < n; i++)
        {
            IntPtr a = Slot(p, i);
            Console.WriteLine("    slot[" + i + "] = 0x" + a.ToInt64().ToString("X16")
                              + "   module=" + ModuleOf(a));
        }
        Console.WriteLine("");
    }

    static string ModuleOf(IntPtr addr)
    {
        if (addr == IntPtr.Zero) return "null";
        IntPtr h = IntPtr.Zero;
        uint flags = 0x00000004 | 0x00000002; // FROM_ADDRESS | UNCHANGED_REFCOUNT
        if (!GetModuleHandleExW(flags, addr, out h) || h == IntPtr.Zero) return "(no module)";
        return "(module@0x" + h.ToInt64().ToString("X") + ")";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetModuleHandleExW(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);

    static void CallSlot(IntPtr p, int i)
    {
        IntPtr addr = Slot(p, i);
        if (addr == IntPtr.Zero)
        {
            Console.WriteLine("  slot[" + i + "] -> NULL, skipped");
            return;
        }
        try
        {
            Fn4 fn = (Fn4)Marshal.GetDelegateForFunctionPointer(addr, typeof(Fn4));
            int r = fn(p, IntPtr.Zero, 0, 0);
            Console.WriteLine("  slot[" + i + "] (0x" + addr.ToInt64().ToString("X")
                              + ") returned 0x" + r.ToString("X8"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  slot[" + i + "] THREW " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
