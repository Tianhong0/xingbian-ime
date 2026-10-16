// slotcall.cs -- call ONE vtable slot of the CCW for Xingbian.Tsf.TextService and
// report the HRESULT. Run one slot per process: a bad slot takes the whole process
// down, so the driver script records the exit code instead of a return value.
//
// This is the A/B instrument for the ITfTextInputProcessorEx defect: the shipped
// DLL exposes 1 method where COM requires 3, so TSF's call to ActivateEx lands on
// garbage. Same source, two DLLs (real vs flattened-interface rebuild).
//
// usage: slotcall.exe <asmPath> <typeName> <interface> <slot> [nargs]
//   interface: IEx | TIP | KES
//   slot     : absolute vtable index
//   nargs    : 1..4 trailing args after `this` (all zero / NULL). default 3.
//
// exit codes: 0 = call returned normally, 0xC0000005 = access violation, other = CLR error.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class SlotCall
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandleExW(uint flags, IntPtr name, out IntPtr mod);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetModuleFileNameW(IntPtr mod, System.Text.StringBuilder buf, uint size);

    static readonly Guid IID_IEx = new Guid("6E4E2102-F9CD-433D-B496-303CE03A6507");
    static readonly Guid IID_TIP = new Guid("AA80E7F7-2021-11D2-93E0-0060B067B86E");
    static readonly Guid IID_KES = new Guid("AA80E7F5-2021-11D2-93E0-0060B067B86E");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int Fn1(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int Fn2(IntPtr self, IntPtr a);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int Fn3(IntPtr self, IntPtr a, IntPtr b);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int Fn4(IntPtr self, IntPtr a, IntPtr b, IntPtr c);

    static IntPtr Slot(IntPtr p, int i)
    {
        IntPtr vt = Marshal.ReadIntPtr(p);
        return Marshal.ReadIntPtr(vt, i * IntPtr.Size);
    }

    static string ModuleOf(IntPtr a)
    {
        if (a == IntPtr.Zero) return "(null)";
        IntPtr m;
        if (GetModuleHandleExW(0x4 /*FROM_ADDRESS*/ | 0x2 /*UNCHANGED_REFCOUNT*/, a, out m) == IntPtr.Zero)
        {
            // Address is not inside any loaded image: a JIT thunk-heap stub, or garbage.
            bool low = a.ToInt64() > 0 && a.ToInt64() < 0x0000800000000000L;
            return low ? "(jit thunk)" : "(NOT AN ADDRESS - garbage)";
        }
        var sb = new System.Text.StringBuilder(1024);
        GetModuleFileNameW(m, sb, 1024);
        return Path.GetFileName(sb.ToString());
    }

    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL " + ex.GetType().Name + ": " + ex.Message);
            return 99;
        }
    }

    static int Run(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("usage: slotcall.exe <asmPath> <typeName> <IEx|TIP|KES> <slot> [nargs]");
            return 2;
        }
        string asmPath = args[0];
        string typeName = args[1];
        string iface = args[2];
        int slot = int.Parse(args[3]);
        int nargs = args.Length > 4 ? int.Parse(args[4]) : 3;

        Guid iid;
        if (iface == "IEx") iid = IID_IEx;
        else if (iface == "TIP") iid = IID_TIP;
        else if (iface == "KES") iid = IID_KES;
        else { Console.WriteLine("unknown interface " + iface); return 2; }

        Console.WriteLine("assembly : " + asmPath);
        Console.WriteLine("interface: " + iface + " {" + iid.ToString().ToUpperInvariant() + "}"
                          + "  slot=" + slot + " nargs=" + nargs);

        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string simple = new AssemblyName(e.Name).Name;
            string cand = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(asmPath)), simple + ".dll");
            if (File.Exists(cand)) return Assembly.Load(File.ReadAllBytes(cand));
            return null;
        };

        Assembly asm = Assembly.Load(File.ReadAllBytes(asmPath));
        Type t = asm.GetType(typeName, true);
        object o = Activator.CreateInstance(t);

        Guid g = iid;
        IntPtr p;
        int hr = Marshal.QueryInterface(Marshal.GetIUnknownForObject(o), ref g, out p);
        Console.WriteLine("QI hr=0x" + hr.ToString("X8") + "  ptr=0x" + p.ToInt64().ToString("X"));
        if (hr != 0 || p == IntPtr.Zero) return 3;

        Console.WriteLine("--- the method array as the CLR actually laid it out ---");
        for (int i = 3; i <= 8; i++)
        {
            IntPtr a = Slot(p, i);
            Console.WriteLine("  slot[" + i + "] = 0x" + a.ToInt64().ToString("X16")
                              + "   " + ModuleOf(a) + (i == slot ? "   <== WE WILL CALL THIS" : ""));
        }

        IntPtr fn = Slot(p, slot);
        Console.WriteLine("CALLING slot[" + slot + "] at 0x" + fn.ToInt64().ToString("X16")
                          + "  (" + ModuleOf(fn) + ")");

        int r;
        if (nargs <= 1)
        {
            Fn1 f = (Fn1)Marshal.GetDelegateForFunctionPointer(fn, typeof(Fn1));
            r = f(p);
        }
        else if (nargs == 2)
        {
            Fn2 f = (Fn2)Marshal.GetDelegateForFunctionPointer(fn, typeof(Fn2));
            r = f(p, IntPtr.Zero);
        }
        else if (nargs == 3)
        {
            Fn3 f = (Fn3)Marshal.GetDelegateForFunctionPointer(fn, typeof(Fn3));
            r = f(p, IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            Fn4 f = (Fn4)Marshal.GetDelegateForFunctionPointer(fn, typeof(Fn4));
            r = f(p, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        Console.WriteLine("RETURNED hr=0x" + r.ToString("X8") + "  (" + r + ")");
        return 0;
    }
}
