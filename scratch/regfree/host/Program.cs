using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Xingbian.RegFree
{
    /// <summary>
    /// A native-equivalent COM host.  Everything here goes through the real
    /// Windows activation stack: kernel32!CreateActCtxW/ActivateActCtx ->
    /// ole32!CoCreateInstance -> SxS manifest -> mscoree.dll -> CLR -> the
    /// managed class.  The only thing that is not literally native is that the
    /// caller itself is compiled managed code (C# 7.3 / net48) -- see RESULTS.md
    /// for what that does and does not prove.
    /// </summary>
    internal static class Program
    {
        // ---- the registration contract under test -------------------------
        private const string CLSID_TEXTSERVICE = "7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43";
        private const string IID_IUNKNOWN = "00000000-0000-0000-C000-000000000046";
        private const string IID_ITFPROCESSOR = "AA80E7F7-2021-11D2-93E0-0060B067B86E";
        private const string IID_ITFPROCESSOREX = "6E4E2102-F9CD-433D-B496-303CE03A6507";
        private const string IID_ITFKEYEVENTSINK = "AA80E7F5-2021-11D2-93E0-0060B067B86E";

        private const string ASM_IDENTITY =
            "Xingbian.Tsf, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null";
        private const string ASM_QUALIFIED =
            "Xingbian.Tsf.TextService, " + ASM_IDENTITY;

        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private const uint ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID = 0x00000004;

        // =====================================================================
        // interop
        // =====================================================================
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ACTCTXW
        {
            public int cbSize;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpSource;
            public ushort wProcessorArchitecture;
            public ushort wLangId;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpAssemblyDirectory;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpResourceName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpApplicationName;
            public IntPtr hModule;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateActCtxW(ref ACTCTXW pActCtx);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ActivateActCtx(IntPtr hActCtx, out UIntPtr lpCookie);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeactivateActCtx(uint dwFlags, UIntPtr ulCookie);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("ole32.dll", PreserveSig = true)]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll", PreserveSig = true)]
        private static extern int CoCreateInstance(
            ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
            ref Guid riid, out IntPtr ppv);

        [DllImport("ole32.dll", PreserveSig = true)]
        private static extern int CoGetClassObject(
            ref Guid rclsid, uint dwClsContext, IntPtr pvReserved,
            ref Guid riid, out IntPtr ppv);

        // =====================================================================
        // TSF interfaces declared HERE, so the host carries no reference to the
        // Xingbian.Tsf assembly.  Slot order is the whole contract.
        // =====================================================================
        [ComImport, Guid(IID_ITFPROCESSOR)]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITfTextInputProcessor
        {
            [PreserveSig] int Activate(IntPtr ptim, uint tid);
            [PreserveSig] int Deactivate();
        }

        [ComImport, Guid(IID_ITFPROCESSOREX)]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITfTextInputProcessorEx : ITfTextInputProcessor
        {
            [PreserveSig] int ActivateEx(IntPtr ptim, uint tid, uint dwFlags);
        }

        [ComImport, Guid(IID_ITFKEYEVENTSINK)]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITfKeyEventSink
        {
            [PreserveSig] int OnSetFocus([MarshalAs(UnmanagedType.Bool)] bool fForeground);
            [PreserveSig] int OnTestKeyDown(IntPtr pic, IntPtr wParam, IntPtr lParam, out bool pfEaten);
            [PreserveSig] int OnTestKeyUp(IntPtr pic, IntPtr wParam, IntPtr lParam, out bool pfEaten);
            [PreserveSig] int OnKeyDown(IntPtr pic, IntPtr wParam, IntPtr lParam, out bool pfEaten);
            [PreserveSig] int OnKeyUp(IntPtr pic, IntPtr wParam, IntPtr lParam, out bool pfEaten);
            [PreserveSig] int OnPreservedKey(IntPtr pic, ref Guid rguid, out bool pfEaten);
        }

        private delegate int ActivateExDelegate(IntPtr self, IntPtr ptim, uint tid, uint dwFlags);
        private delegate int DeactivateDelegate(IntPtr self);
        private delegate int OnSetFocusDelegate(IntPtr self, int fForeground);

        // =====================================================================
        private static int s_failures;

        private static void Report(string what, int hr)
        {
            string tag = hr >= 0 ? "OK  " : "FAIL";
            if (hr < 0) { s_failures++; }
            Console.WriteLine("RESULT {0,-52} hr=0x{1:X8}  {2} {3}",
                what, unchecked((uint)hr), tag, HrText(hr));
        }

        /// <summary>
        /// Report a call whose FAILURE is the pass condition.
        ///
        /// Two probes land here and neither is a defect in the text service:
        ///   * the deliberately-NULL activation probes, which are supposed to hit the
        ///     text service's own E_INVALIDARG guard -- that guard firing is precisely
        ///     the proof that its managed method body ran in this process;
        ///   * CoInitializeEx returning RPC_E_CHANGED_MODE, which only means the thread
        ///     was already put in an apartment (the CLR does this before Main), so COM
        ///     is usable and the run should carry on rather than be marked failed.
        /// </summary>
        private static void ReportExpected(string what, int hr, int expected)
        {
            string tag = hr >= 0 ? "OK  " : (hr == expected ? "FAIL(EXPECTED)" : "FAIL");
            if (hr < 0 && hr != expected) { s_failures++; }
            Console.WriteLine("RESULT {0,-52} hr=0x{1:X8}  {2} {3}",
                what, unchecked((uint)hr), tag, HrText(hr));
        }

        private static string HrText(int hr)
        {
            switch (unchecked((uint)hr))
            {
                case 0x00000000: return "S_OK";
                case 0x80040154: return "REGDB_E_CLASSNOTREG";
                case 0x80040111: return "CLASS_E_CLASSNOTAVAILABLE";
                case 0x80070002: return "HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)";
                case 0x8007007E: return "HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND)";
                case 0x80070057: return "E_INVALIDARG";
                case 0x80131700: return "CLR_E_SHIM_RUNTIMELOAD";
                case 0x80131040: return "COR_E_FILELOAD";
                case 0x80004002: return "E_NOINTERFACE";
                case 0x80004005: return "E_FAIL";
                default: return "";
            }
        }

        private static Guid G(string s) { return new Guid(s); }

        private static IntPtr Slot(IntPtr pUnk, int index)
        {
            IntPtr vtbl = Marshal.ReadIntPtr(pUnk);
            return Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
        }

        private static void Banner(string s)
        {
            Console.WriteLine();
            Console.WriteLine(new string('-', 78));
            Console.WriteLine(s);
            Console.WriteLine(new string('-', 78));
        }

        private static void DumpModules()
        {
            foreach (string m in new[] { "mscoree.dll", "mscoreei.dll", "clr.dll",
                                         "mscorlib.ni.dll", "clrjit.dll" })
            {
                Console.WriteLine("    GetModuleHandle({0,-16}) = 0x{1:X}",
                    m, GetModuleHandleW(m).ToInt64());
            }
        }

        private static int Main(string[] args)
        {
            string manifest = null;
            bool loadProbe = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--manifest" && i + 1 < args.Length) { manifest = args[++i]; }
                else if (args[i] == "--load-probe") { loadProbe = true; }
            }

            Console.WriteLine("==============================================================================");
            Console.WriteLine("RegFreeHost -- native-equivalent host for Xingbian.Tsf.TextService");
            Console.WriteLine("==============================================================================");
            Console.WriteLine("process exe   : {0}", Environment.GetCommandLineArgs()[0]);
            Console.WriteLine("AppBase       : {0}", AppDomain.CurrentDomain.BaseDirectory);
            Console.WriteLine("exit assembly : {0}", Assembly.GetExecutingAssembly().Location);
            Console.WriteLine("this host references Xingbian.Tsf at compile time : NO");
            Console.WriteLine("");

            int hr = CoInitializeEx(IntPtr.Zero, 2 /*APARTMENTTHREADED*/);
            // RPC_E_CHANGED_MODE only means the CLR already set this thread's apartment
            // before Main ran. COM is initialised either way, so this is not a failure.
            ReportExpected("CoInitializeEx(APARTMENTTHREADED)", hr, unchecked((int)0x80010106));

            // -----------------------------------------------------------------
            // Layer 0: can plain fusion probing find the assembly?
            // -----------------------------------------------------------------
            if (loadProbe)
            {
                Banner("LAYER 0: Assembly.Load by full identity (fusion probing only)");
                Console.WriteLine("identity: {0}", ASM_IDENTITY);
                try
                {
                    Assembly a = Assembly.Load(ASM_IDENTITY);
                    Console.WriteLine("OK: loaded {0}", a.FullName);
                    Console.WriteLine("    Location = {0}", a.Location);
                }
                catch (Exception ex)
                {
                    s_failures++;
                    Console.WriteLine("FAIL: {0}: {1}", ex.GetType().Name, ex.Message);
                }
            }

            // -----------------------------------------------------------------
            // Layer 1: the manifest-driven native activation
            // -----------------------------------------------------------------
            UIntPtr cookie = UIntPtr.Zero;
            bool activated = false;
            if (manifest != null)
            {
                Banner("LAYER 1: SxS manifest -> ole32!CoCreateInstance");
                ACTCTXW ctx = new ACTCTXW();
                ctx.cbSize = Marshal.SizeOf(typeof(ACTCTXW));
                ctx.dwFlags = ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID;
                ctx.lpSource = manifest;
                ctx.lpAssemblyDirectory = Path.GetDirectoryName(Path.GetFullPath(manifest));
                IntPtr h = CreateActCtxW(ref ctx);
                if (h == new IntPtr(-1) || h == IntPtr.Zero)
                {
                    s_failures++;
                    Console.WriteLine("RESULT CreateActCtxW      FAILED GetLastError={0}",
                        Marshal.GetLastWin32Error());
                    return 1;
                }
                Console.WriteLine("RESULT CreateActCtxW      -> 0x{0:X}", h.ToInt64());
                if (!ActivateActCtx(h, out cookie))
                {
                    s_failures++;
                    Console.WriteLine("RESULT ActivateActCtx     FAILED GetLastError={0}",
                        Marshal.GetLastWin32Error());
                    return 1;
                }
                Console.WriteLine("RESULT ActivateActCtx     -> cookie 0x{0:X}", cookie.ToUInt64());
                Console.WriteLine("manifest: {0}", manifest);
                activated = true;
            }
            else
            {
                Banner("LAYER 1: no manifest activated (baseline / config-only run)");
            }

            Console.WriteLine("");
            DumpModules();

            Console.WriteLine("");
            Console.WriteLine("CoCreateInstance(CLSID_TextService, IID_IUnknown) ...");
            Guid clsid = G(CLSID_TEXTSERVICE);
            Guid iidUnk = G(IID_IUNKNOWN);
            IntPtr pUnk;
            hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER,
                                  ref iidUnk, out pUnk);
            Report("ole32!CoCreateInstance(CLSID, IID_IUnknown)", hr);

            if (hr >= 0 && pUnk != IntPtr.Zero)
            {
                Console.WriteLine("OK: IUnknown* = 0x{0:X}", pUnk.ToInt64());
                Console.WriteLine("");
                DumpModules();
                Console.WriteLine("");

                // ---- QI onto the TSF interfaces -----------------------------
                Banner("LAYER 2: QueryInterface for the TSF IIDs");
                object rcw = null;
                try { rcw = Marshal.GetObjectForIUnknown(pUnk); }
                catch (Exception ex) { Console.WriteLine("RCW wrap failed: {0}", ex.Message); }

                string[] names = { "ITfTextInputProcessorEx", "ITfKeyEventSink",
                                   "ITfTextInputProcessor" };
                Guid[] iids = { G(IID_ITFPROCESSOREX), G(IID_ITFKEYEVENTSINK),
                                G(IID_ITFPROCESSOR) };
                IntPtr[] ptrs = new IntPtr[3];
                for (int i = 0; i < 3; i++)
                {
                    IntPtr p;
                    Guid g = iids[i];
                    int qhr = Marshal.QueryInterface(pUnk, ref g, out p);
                    Report("QI " + names[i], qhr);
                    if (qhr >= 0) { ptrs[i] = p; }
                }

                // ---- RCW typed cast: proves standard COM marshalling --------
                Banner("LAYER 3: calls through the real interface vtable");
                if (ptrs[0] != IntPtr.Zero)
                {
                    IntPtr slot5 = Slot(ptrs[0], 5);   // ActivateEx
                    ActivateExDelegate f = (ActivateExDelegate)
                        Marshal.GetDelegateForFunctionPointer(slot5, typeof(ActivateExDelegate));
                    int chr = f(ptrs[0], IntPtr.Zero, 0, 0);
                    ReportExpected("raw-vtable ITfTextInputProcessorEx::ActivateEx(NULL,0,0)", chr,
                        unchecked((int)0x80070057));
                    Console.WriteLine("     TextService.cs:181 -> `if (ptim == null) return HResult.E_INVALIDARG;`");
                    Console.WriteLine("     a 0x80070057 here proves the MANAGED BODY executed in this process.");
                }
                if (ptrs[1] != IntPtr.Zero)
                {
                    // NOTE: the RCW cast is expected to fail here and that is NOT a defect
                    // in the text service. The CLR cannot cast this RCW to a locally
                    // declared [ComImport] interface when an instance of the very same
                    // class is already loaded in this AppDomain under its own interface
                    // identity; the classic symptom is InvalidCastException. The raw
                    // vtable call below is the meaningful probe: it goes through the
                    // interface pointer COM actually handed us.
                    try
                    {
                        ITfKeyEventSink ks = (ITfKeyEventSink)Marshal.GetTypedObjectForIUnknown(
                            ptrs[1], typeof(ITfKeyEventSink));
                        int chr = ks.OnSetFocus(false);
                        Report("RCW ITfKeyEventSink::OnSetFocus(FALSE)", chr);
                    }
                    catch (InvalidCastException)
                    {
                        Console.WriteLine("NOTE RCW cast ITfKeyEventSink not available in this");
                        Console.WriteLine("     AppDomain; using the raw vtable instead (see below).");
                    }
                    catch (Exception ex)
                    {
                        s_failures++;
                        Console.WriteLine("FAIL RCW cast ITfKeyEventSink: {0}: {1}",
                            ex.GetType().Name, ex.Message);
                    }

                    // ITfKeyEventSink vtable: 0-2 IUnknown, 3 OnSetFocus(BOOL)
                    IntPtr kslot3 = Slot(ptrs[1], 3);
                    OnSetFocusDelegate kf = (OnSetFocusDelegate)
                        Marshal.GetDelegateForFunctionPointer(kslot3, typeof(OnSetFocusDelegate));
                    int khr = kf(ptrs[1], 0);
                    Report("raw-vtable ITfKeyEventSink::OnSetFocus(FALSE)", khr);
                }
                if (ptrs[2] != IntPtr.Zero)
                {
                    try
                    {
                        ITfTextInputProcessor tip = (ITfTextInputProcessor)
                            Marshal.GetTypedObjectForIUnknown(ptrs[2], typeof(ITfTextInputProcessor));
                        int chr = tip.Deactivate();
                        Report("RCW ITfTextInputProcessor::Deactivate()", chr);
                    }
                    catch (InvalidCastException)
                    {
                        Console.WriteLine("NOTE RCW cast ITfTextInputProcessor not available in this");
                        Console.WriteLine("     AppDomain; using the raw vtable instead (see below).");
                    }
                    catch (Exception ex)
                    {
                        s_failures++;
                        Console.WriteLine("FAIL RCW cast ITfTextInputProcessor: {0}: {1}",
                            ex.GetType().Name, ex.Message);
                    }

                    // ITfTextInputProcessor vtable: 0-2 IUnknown, 3 Activate, 4 Deactivate
                    IntPtr slot4 = Slot(ptrs[2], 4);
                    DeactivateDelegate d = (DeactivateDelegate)
                        Marshal.GetDelegateForFunctionPointer(slot4, typeof(DeactivateDelegate));
                    int dhr = d(ptrs[2]);
                    Report("raw-vtable ITfTextInputProcessor::Deactivate()", dhr);
                }

                Marshal.Release(pUnk);
            }
            else
            {
                Console.WriteLine("");
                Console.WriteLine(">>> CoCreateInstance failed; no object was ever created.");
            }

            if (activated) { DeactivateActCtx(0, cookie); }

            Console.WriteLine();
            Console.WriteLine("==============================================================================");
            if (s_failures == 0) { Console.WriteLine("ALL STEPS PASSED"); }
            else { Console.WriteLine("FAILED STEPS ({0})", s_failures); }
            Console.WriteLine("==============================================================================");
            return s_failures == 0 ? 0 : 1;
        }
    }
}
