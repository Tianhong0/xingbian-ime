// vtdump.cs -- MANAGED HOST: dump and exercise the raw CCW vtables of a .NET class.
//
// Purpose: given an assembly path + type name, produce the SAME vtable evidence a
// native host sees, so a patched build can be compared against the shipped one
// without touching the machine registry (which we are not allowed to write).
//
// LIMITATION, stated plainly: the caller here lives inside the CLR. Only the
// *slot discovery/call* is native -- the pointers are real CCW method pointers and
// are invoked through Marshal.GetDelegateForFunctionPointer. The native-process
// evidence is produced separately by scratch\regfree\src\slot_probe.py.
//
// Build:
//   csc /nologo /target:exe /out:vtdump.exe vtdump.cs
// Usage:
//   vtdump.exe <assembly-path> <type-full-name>

using System;
using System.Reflection;
using System.Runtime.InteropServices;

public static class Program
{
    delegate int Fn1(IntPtr self);
    delegate int Fn4(IntPtr self, IntPtr a, uint b, uint c);

    static readonly Guid IID_TIP = new Guid("aa80e7f7-2021-11d2-93e0-0060b067b86e");
    static readonly Guid IID_TIPEX = new Guid("6e4e2102-f9cd-433d-b496-303ce03a6507");
    static readonly Guid IID_KES = new Guid("aa80e7f5-2021-11d2-93e0-0060b067b86e");

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetModuleHandleExW(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);

    static IntPtr VtblOf(IntPtr p) { return Marshal.ReadIntPtr(p); }
    static IntPtr Slot(IntPtr p, int i) { return Marshal.ReadIntPtr(VtblOf(p), i * IntPtr.Size); }

    static string ModuleOf(IntPtr addr)
    {
        if (addr == IntPtr.Zero) return "null";
        IntPtr h;
        if (!GetModuleHandleExW(0x4 | 0x2, addr, out h) || h == IntPtr.Zero) return "(no module)";
        try
        {
            System.Diagnostics.ProcessModule m =
                System.Diagnostics.Process.GetCurrentProcess().MainModule;
            foreach (System.Diagnostics.ProcessModule pm
                     in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                if (pm.BaseAddress == h) return pm.ModuleName;
            }
        }
        catch { }
        return "(module@0x" + h.ToInt64().ToString("X") + ")";
    }

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // Print manually: this process loads Xingbian.Tsf.dll whose interop
            // types may fail to resolve, and Exception.ToString() can itself throw.
            Console.WriteLine("[FATAL] " + ex.GetType().FullName);
            Console.WriteLine("[FATAL] message: " + ex.Message);
            Exception inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth < 5)
            {
                Console.WriteLine("[FATAL]  inner[" + depth + "] " + inner.GetType().FullName
                                  + ": " + inner.Message);
                inner = inner.InnerException;
                depth++;
            }
            ReflectionTypeLoadException rtle = ex as ReflectionTypeLoadException;
            if (rtle != null)
            {
                for (int i = 0; i < rtle.LoaderExceptions.Length; i++)
                {
                    Exception le = rtle.LoaderExceptions[i];
                    Console.WriteLine("[FATAL]  loader[" + i + "] "
                                      + (le == null ? "null" : le.GetType().FullName + ": " + le.Message));
                }
            }
            return 1;
        }
    }

    static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: vtdump.exe <assembly-path> <type-full-name>");
            return 2;
        }
        string asmPath = args[0];
        string typeName = args[1];

        Console.WriteLine("==============================================================");
        Console.WriteLine("vtdump.exe  assembly=" + asmPath);
        Console.WriteLine("            type=" + typeName);
        Console.WriteLine("==============================================================");

        Assembly asm;
        string asmDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(asmPath));
        // The checkout carries a Mark-of-the-Web / network-location mark, so
        // Assembly.LoadFrom() is refused (0x80131515). Load the bytes directly and
        // resolve sibling dependencies (Xingbian.Core.dll) from the same folder.
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string simple = new AssemblyName(e.Name).Name;
            string cand = System.IO.Path.Combine(asmDir, simple + ".dll");
            if (System.IO.File.Exists(cand))
                return Assembly.Load(System.IO.File.ReadAllBytes(cand));
            return null;
        };
        asm = Assembly.Load(System.IO.File.ReadAllBytes(asmPath));
        Console.WriteLine("loaded: " + asm.FullName);
        Console.WriteLine("ImageRuntimeVersion: " + asm.ImageRuntimeVersion);

        // ---- metadata shape: how many methods does each interface DECLARE? ----
        Console.WriteLine("");
        Console.WriteLine("--- declared interface shapes (metadata) ---");
        foreach (Type t in asm.GetTypes())
        {
            if (!t.IsInterface || !t.IsPublic) continue;
            MethodInfo[] own = t.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                           | BindingFlags.DeclaredOnly);
            Type[] bases = t.GetInterfaces();
            Console.WriteLine("  " + t.Name
                              + "  declaredMethods=" + own.Length
                              + "  baseInterfaces=" + bases.Length
                              + "  IsComImport=" + t.IsImport);
            foreach (MethodInfo mi in own)
                Console.WriteLine("        " + mi.ReturnType.Name + " " + mi.Name
                                  + "(" + Params(mi) + ")");
        }

        Type type = asm.GetType(typeName, true);
        object obj = Activator.CreateInstance(type);
        IntPtr unk = Marshal.GetIUnknownForObject(obj);
        Console.WriteLine("");
        Console.WriteLine("GetIUnknownForObject(" + typeName + ") = 0x"
                          + unk.ToInt64().ToString("X"));

        Check(unk, "ITfTextInputProcessor  ", IID_TIP, 5, 2);
        Console.WriteLine("");
        Check(unk, "ITfTextInputProcessorEx", IID_TIPEX, 7, 3);
        Console.WriteLine("");
        Check(unk, "ITfKeyEventSink        ", IID_KES, 12, 6);
        return 0;
    }

    static string Params(MethodInfo mi)
    {
        ParameterInfo[] ps = mi.GetParameters();
        string s = "";
        for (int i = 0; i < ps.Length; i++)
        {
            if (i > 0) s += ", ";
            s += ps[i].ParameterType.Name + " " + ps[i].Name;
        }
        return s;
    }

    static void Check(IntPtr unk, string label, Guid iid, int dumpTo, int expectMethods)
    {
        Guid g = iid;
        IntPtr p = IntPtr.Zero;
        int hr = Marshal.QueryInterface(unk, ref g, out p);
        Console.WriteLine("==============================================================");
        Console.WriteLine(label + " {" + iid.ToString().ToUpperInvariant() + "}");
        Console.WriteLine("  QI hr=0x" + hr.ToString("X8") + "  ptr=0x" + p.ToInt64().ToString("X")
                          + "   EXPECTED methods=" + expectMethods);
        if (hr != 0 || p == IntPtr.Zero) { Console.WriteLine("  QI failed"); return; }

        int real = 0;
        bool ended = false;
        for (int i = 3; i <= dumpTo; i++)
        {
            IntPtr a = Slot(p, i);
            string mod = ModuleOf(a);
            // The CLR writes the interface's method thunks contiguously right after
            // the this-pointer block, then terminates the array with a pointer into
            // clr.dll. The thunks themselves live in a JIT thunk heap that is not
            // backed by any module image, so ModuleOf() reports "(no module)" for
            // them -- the clr.dll entry is the reliable end-of-array marker.
            bool isTerminator = ended || a == IntPtr.Zero
                                || mod.IndexOf("clr", StringComparison.OrdinalIgnoreCase) >= 0
                                || mod.IndexOf("mscorwks", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isTerminator) ended = true;
            Console.WriteLine("    slot[" + i + "] = 0x" + a.ToInt64().ToString("X16")
                              + "   " + mod + (ended ? "   <-- END OF METHOD ARRAY" : ""));
            if (i >= 3 && !ended) real++;
        }
        Console.WriteLine("  --> methods before the array terminator, counted from slot 3: "
                          + real + "   (expected " + expectMethods + ")"
                          + (real == expectMethods ? "   OK" : "   *** MISMATCH ***"));

        Console.WriteLine("  calling slot[3] with 4 args (this, NULL, 0, 0):");
        try
        {
            IntPtr a = Slot(p, 3);
            Fn4 fn = (Fn4)Marshal.GetDelegateForFunctionPointer(a, typeof(Fn4));
            int r = fn(p, IntPtr.Zero, 0, 0);
            Console.WriteLine("    slot[3] returned 0x" + r.ToString("X8"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("    slot[3] THREW " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
