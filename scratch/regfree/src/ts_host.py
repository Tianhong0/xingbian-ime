#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
ts_host.py -- NATIVE (non-CLR) activation probe for Xingbian.Tsf.TextService.

The interpreter hosting this script is python.exe: a plain Win32 process with no
CLR loaded.  Everything below therefore happens on the native side of the COM
boundary:

    * create ActCtx from a registration-free COM manifest that carries <clrClass>
    * ole32!CoCreateInstance(CLSID_TextService, IID_IUnknown)
    * QueryInterface for the real TSF IIDs
    * actually CALL methods through the returned vtables
    * mscoree!DllGetClassObject / CLRCreateInstance for the fallback paths

Run one variant per process (activation contexts and the CLR bind per-process):
    python ts_host.py --variant V1_single_file
    python ts_host.py --variant baseline_nomanifest
    python ts_host.py --variant mscoree
    python ts_host.py --variant clrcreateinstance
"""

import ctypes
import os
import sys
import uuid
import argparse
from ctypes import wintypes

HERE = os.path.dirname(os.path.abspath(__file__))
APP_DIR = os.path.join(os.path.dirname(HERE), "app")
MANIFEST_DIR = os.path.join(HERE, "manifests")


# ----------------------------------------------------------------------------
# GUID plumbing
# ----------------------------------------------------------------------------
class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", ctypes.c_uint32),
        ("Data2", ctypes.c_uint16),
        ("Data3", ctypes.c_uint16),
        ("Data4", ctypes.c_ubyte * 8),
    ]

    def __str__(self):
        return "{%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}" % (
            self.Data1, self.Data2, self.Data3,
            self.Data4[0], self.Data4[1], self.Data4[2], self.Data4[3],
            self.Data4[4], self.Data4[5], self.Data4[6], self.Data4[7],
        )


def guid(s):
    u = uuid.UUID(s)
    g = GUID()
    g.Data1 = u.time_low
    g.Data2 = u.time_mid
    g.Data3 = u.time_hi_version
    g.Data4 = (ctypes.c_ubyte * 8)(*u.bytes[8:])
    return g


# --- the identifiers under test ---------------------------------------------
CLSID_TEXTSERVICE = "7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43"
IID_IUNKNOWN = "00000000-0000-0000-C000-000000000046"
IID_ICLASSFACTORY = "00000001-0000-0000-C000-000000000046"
IID_ITfTextInputProcessorEx = "6E4E2102-F9CD-433D-B496-303CE03A6507"
IID_ITfKeyEventSink = "AA80E7F5-2021-11D2-93E0-0060B067B86E"
IID_ITfTextInputProcessor = "AA80E7F7-2021-11D2-93E0-0060B067B86E"

CLSID_TF_ThreadMgr = "529A9E6B-6587-4F23-AB9E-9C7D683E3C50"
IID_ITfThreadMgr = "AA80E801-2021-11D2-93E0-0060B067B86E"
IID_ITfThreadMgrEx = "3E90ADE3-7594-4CB0-BB58-69628F5F091B"

CLSID_CLRMetaHost = "9280188D-0E8E-4867-B30C-7FA83884E8DE"
IID_ICLRMetaHost = "D332DB9E-B9B3-4125-8207-A14884F53216"
IID_ICLRRuntimeInfo = "BD39D1D2-BA2F-486A-89B0-B4B0CB466891"
CLSID_CLRRuntimeHost = "90F1A06E-7712-4762-86B5-7A5EBA6BDB02"
IID_ICLRRuntimeHost = "90F1A06C-7712-4762-86B5-7A5EBA6BDB02"

# --- context flags ----------------------------------------------------------
CLSCTX_INPROC_SERVER = 0x1

# --- ActCtx -----------------------------------------------------------------
ACTCTX_FLAG_PROCESSOR_ARCHITECTURE_VALID = 0x00000001
ACTCTX_FLAG_LANGID_VALID = 0x00000002
ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID = 0x00000004
ACTCTX_FLAG_RESOURCE_NAME_VALID = 0x00000008
ACTCTX_FLAG_SET_PROCESS_DEFAULT = 0x00000010
ACTCTX_FLAG_APPLICATION_NAME_VALID = 0x00000020


class ACTCTXW(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_uint32),
        ("dwFlags", ctypes.c_uint32),
        ("lpSource", wintypes.LPCWSTR),
        ("wProcessorArchitecture", ctypes.c_uint16),
        ("wLangId", ctypes.c_uint16),
        ("lpAssemblyDirectory", wintypes.LPCWSTR),
        ("lpResourceName", wintypes.LPCWSTR),
        ("lpApplicationName", wintypes.LPCWSTR),
        ("hModule", wintypes.HMODULE),
    ]


kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
ole32 = ctypes.WinDLL("ole32", use_last_error=True)

kernel32.CreateActCtxW.argtypes = [ctypes.POINTER(ACTCTXW)]
kernel32.CreateActCtxW.restype = wintypes.HANDLE
kernel32.ActivateActCtx.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_size_t)]
kernel32.ActivateActCtx.restype = wintypes.BOOL
kernel32.DeactivateActCtx.argtypes = [ctypes.c_uint32, ctypes.c_size_t]
kernel32.DeactivateActCtx.restype = wintypes.BOOL
kernel32.ReleaseActCtx.argtypes = [wintypes.HANDLE]
kernel32.ReleaseActCtx.restype = None
kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
kernel32.GetModuleHandleW.restype = wintypes.HMODULE

ole32.CoInitializeEx.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
ole32.CoInitializeEx.restype = ctypes.c_int32
ole32.CoCreateInstance.argtypes = [
    ctypes.POINTER(GUID), ctypes.c_void_p, ctypes.c_uint32,
    ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p),
]
ole32.CoCreateInstance.restype = ctypes.c_int32

INVALID_HANDLE_VALUE = wintypes.HANDLE(-1).value

# ----------------------------------------------------------------------------
# HRESULT naming
# ----------------------------------------------------------------------------
HR_NAMES = {
    0x00000000: "S_OK",
    0x00000001: "S_FALSE",
    0x80004001: "E_NOTIMPL",
    0x80004002: "E_NOINTERFACE",
    0x80004005: "E_FAIL",
    0x80040111: "CLASS_E_CLASSNOTAVAILABLE",
    0x80040154: "REGDB_E_CLASSNOTREG",
    0x800401F0: "CO_E_NOTINITIALIZED",
    0x80070002: "HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)",
    0x80070005: "E_ACCESSDENIED",
    0x8007000B: "HRESULT_FROM_WIN32(ERROR_BAD_FORMAT)",
    0x80070057: "E_INVALIDARG",
    0x8007007E: "HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND)",
    0x8007007F: "HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND)",
    0x80029C4A: "TYPE_E_CANTLOADLIBRARY",
    0x80131040: "COR_E_FILELOAD / assembly could not be located",
    0x8013101B: "COR_E_NEWER_RUNTIME",
    0x80131500: "COR_E_EXCEPTION",
    0x80131513: "COR_E_TYPELOAD (TypeLoadException)",
    0x80131515: "COR_E_FILELOAD",
    0x80131522: "COR_E_TARGETINVOCATION",
    0x80131604: "COR_E_TARGETPARAMCOUNT",
    0x80131700: "CLR_E_SHIM_RUNTIMELOAD",
}

# HRESULTs that a deliberately-NULL probe is EXPECTED to produce. Seeing them is a
# pass: they can only come from the text service's own argument guard, so they prove
# the managed method body ran in this process.
HResult_E_INVALIDARG = 0x80070057


def u32(hr):
    return hr & 0xFFFFFFFF


def hr_text(hr):
    h = u32(hr)
    return HR_NAMES.get(h, "")


FAIL_BITS = set()
EXPECTED_BITS = set()


def report(label, hr, expected_fail=None):
    """Record one probe result.

    `expected_fail` marks a call whose FAILURE is the pass condition: the NULL
    argument probes are designed to hit the text service's own E_INVALIDARG
    guard, and that guard firing is the proof that managed code ran in this
    native process. Such a probe still prints FAIL (the HRESULT really is a
    failure), but it is tallied under EXPECTED_BITS instead of FAIL_BITS, so a
    green run is not polluted by probes that were never meant to succeed.
    """
    h = u32(hr)
    ok = h < 0x80000000
    name = hr_text(hr)
    tag = "OK" if ok else "FAIL"
    if not ok and expected_fail is not None:
        if h == u32(expected_fail):
            tag = "FAIL(EXPECTED)"
        else:
            tag = "FAIL(UNEXPECTED!)"
    print("RESULT %-58s hr=0x%08X  %s%s" % (label, h, tag, (" " + name) if name else ""))
    sys.stdout.flush()
    if not ok:
        if expected_fail is not None and h == u32(expected_fail):
            EXPECTED_BITS.add(label)
        else:
            FAIL_BITS.add(label)
    return h


def banner(text):
    print("")
    print("-" * 78)
    print(text)
    print("-" * 78)
    sys.stdout.flush()


# ----------------------------------------------------------------------------
# vtable helpers
# ----------------------------------------------------------------------------
def vtbl_entries(p):
    """Return the vtable of a COM interface pointer as a list of callables."""
    addr = ctypes.cast(p, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    return addr


def slot(p, index):
    addr = ctypes.cast(p, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    return addr[index]


def QI(p, iid_str):
    fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.POINTER(GUID),
                            ctypes.POINTER(ctypes.c_void_p))(slot(p, 0))
    out = ctypes.c_void_p(None)
    g = guid(iid_str)
    hr = fn(p, ctypes.byref(g), ctypes.byref(out))
    return u32(hr), out.value


def AddRef(p):
    fn = ctypes.WINFUNCTYPE(ctypes.c_uint32, ctypes.c_void_p)(slot(p, 1))
    return fn(p)


def Release(p):
    fn = ctypes.WINFUNCTYPE(ctypes.c_uint32, ctypes.c_void_p)(slot(p, 2))
    return fn(p)


# ----------------------------------------------------------------------------
# manifest variants
# ----------------------------------------------------------------------------
# ----------------------------------------------------------------------------
# Manifest construction.
#
# SCHEMA FACT (verified against the published manifest file schema and against
# CreateActCtxW itself, see RESULTS.md):
#     <clrClass> is a DIRECT CHILD OF <assembly>.
#     <file>'s only permitted children are comClass / comInterfaceProxyStub /
#     typelib / windowClass.  Putting <clrClass> inside <file> does NOT produce a
#     helpful error -- CreateActCtxW simply fails with GetLastError() == 14001
#     ERROR_SXS_CANT_GEN_ACTCTX, in EVERY namespace (asm.v1 / v2 / v3) and with
#     every attribute subset.  That is the trap this file exists to document.
# ----------------------------------------------------------------------------

XMLDECL = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'

MANIFEST_HEAD = (
    XMLDECL
    + '<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">\n'
    + '  <assemblyIdentity type="win32" name="Xingbian.RegFree.Probe" version="1.0.0.0"\n'
    + '                    processorArchitecture="amd64" />\n'
)
MANIFEST_TAIL = '</assembly>\n'

CLSID_BRACED = "{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}"

# The CLR identity that the *registry* equivalent of this manifest would carry in
# InprocServer32\Assembly.  This is what a real TIP registration names.
ASM_QUALIFIED = ("Xingbian.Tsf.TextService, Xingbian.Tsf, "
                 "Version=0.1.0.0, Culture=neutral, PublicKeyToken=null")

FILE_TSF = '  <file name="Xingbian.Tsf.dll" />\n'
FILE_CORE = '  <file name="Xingbian.Core.dll" />\n'
# A regression probe that names a <file> that will never exist.
FILE_MISSING = '  <file name="Xingbian.Tsf.Missing.dll" />\n'


def clr_class(name, runtime="v4.0.30319", clsid=CLSID_BRACED, threading="Both", indent="  "):
    """Emit a schema-valid <clrClass>, a direct child of <assembly>."""
    return (indent + '<clrClass clsid="%s"\n' % clsid
            + indent + '          threadingModel="%s"\n' % threading
            + indent + '          runtimeVersion="%s"\n' % runtime
            + indent + '          name="%s" />\n' % name)


def manifest_xml(variant):
    """Returns (xml_text, side_car_manifests_dict)."""

    # ---------------- the candidate that should actually work ----------------
    if variant == "V1_toplevel_asmqual":
        xml = (MANIFEST_HEAD + FILE_TSF + FILE_CORE
               + clr_class(ASM_QUALIFIED) + MANIFEST_TAIL)
        return xml, {}

    # ------------- variations that isolate which piece is load-bearing --------
    if variant == "V2_toplevel_shortname":
        # name = plain type name, no assembly qualification
        xml = (MANIFEST_HEAD + FILE_TSF + FILE_CORE
               + clr_class("Xingbian.Tsf.TextService") + MANIFEST_TAIL)
        return xml, {}

    if variant == "V3_toplevel_nofiles":
        # clrClass only -- no <file> elements bind the DLL into the assembly
        xml = (MANIFEST_HEAD + clr_class(ASM_QUALIFIED) + MANIFEST_TAIL)
        return xml, {}

    if variant == "V4_toplevel_nocore":
        # Xingbian.Core.dll (our dependency) is NOT declared as a <file>
        xml = (MANIFEST_HEAD + FILE_TSF
               + clr_class(ASM_QUALIFIED) + MANIFEST_TAIL)
        return xml, {}

    if variant == "V5_toplevel_shortname_nofiles":
        xml = (MANIFEST_HEAD + clr_class("Xingbian.Tsf.TextService") + MANIFEST_TAIL)
        return xml, {}

    # ------------------- side-by-side assembly manifest route ----------------
    # SxS resolves <dependentAssembly> for a PRIVATE assembly by looking for a
    # file named exactly "<assemblyIdentity name>.manifest" (or an embedded
    # RT_MANIFEST resource #2 inside "<name>.dll") in the assembly directory.
    # An invented identity name therefore matches NOTHING on disk.
    if variant in ("V6_dependent_assembly", "V7_private_dllname",
                   "V8_private_dllmanifest"):
        ident = "Xingbian.Tsf.Asm" if variant == "V6_dependent_assembly" else "Xingbian.Tsf"
        side_name = {"V6_dependent_assembly": "Xingbian.Tsf.Asm.manifest",
                     "V7_private_dllname": "Xingbian.Tsf.manifest",
                     "V8_private_dllmanifest": "Xingbian.Tsf.dll.manifest"}[variant]
        xml = (
            XMLDECL
            + '<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">\n'
            + '  <assemblyIdentity type="win32" name="Xingbian.RegFree.Probe" version="1.0.0.0"\n'
            + '                    processorArchitecture="amd64" />\n'
            + '  <dependency>\n'
            + '    <dependentAssembly>\n'
            + '      <assemblyIdentity type="win32" name="%s" version="0.1.0.0"\n' % ident
            + '                        processorArchitecture="amd64" />\n'
            + '    </dependentAssembly>\n'
            + '  </dependency>\n'
            + '</assembly>\n')
        side = {}
        side[side_name] = (
            XMLDECL
            + '<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">\n'
            + '  <assemblyIdentity type="win32" name="%s" version="0.1.0.0"\n' % ident
            + '                    processorArchitecture="amd64" />\n'
            + FILE_TSF + FILE_CORE
            + clr_class(ASM_QUALIFIED)
            + '</assembly>\n')
        return xml, side

    # --------------------------- deliberate breakage -------------------------
    if variant == "E1_bad_classname":
        xml = (MANIFEST_HEAD + FILE_TSF + FILE_CORE
               + clr_class("Xingbian.Tsf.NoSuchClass") + MANIFEST_TAIL)
        return xml, {}

    if variant == "E2_bad_version":
        xml = (MANIFEST_HEAD + FILE_TSF + FILE_CORE
               + clr_class("Xingbian.Tsf.TextService, Xingbian.Tsf, "
                           "Version=9.9.9.9, Culture=neutral, PublicKeyToken=null")
               + MANIFEST_TAIL)
        return xml, {}

    if variant == "E3_no_version":
        # what you get when Assembly= is written WITHOUT the ", Version=..." part
        xml = (MANIFEST_HEAD + FILE_TSF + FILE_CORE
               + clr_class("Xingbian.Tsf.TextService, Xingbian.Tsf") + MANIFEST_TAIL)
        return xml, {}

    if variant == "E4_bad_runtimeversion":
        xml = (MANIFEST_HEAD + FILE_TSF + FILE_CORE
               + clr_class(ASM_QUALIFIED, runtime="v9.9.99999") + MANIFEST_TAIL)
        return xml, {}

    if variant == "E5_missing_dll_file":
        xml = (MANIFEST_HEAD + FILE_MISSING
               + clr_class(ASM_QUALIFIED) + MANIFEST_TAIL)
        return xml, {}

    if variant == "E6_wrong_clsid":
        xml = (MANIFEST_HEAD + FILE_TSF + FILE_CORE
               + clr_class(ASM_QUALIFIED, clsid="{00000000-0000-0000-0000-000000000000}")
               + MANIFEST_TAIL)
        return xml, {}

    raise SystemExit("unknown variant " + variant)


def write_manifests(variant):
    xml, side = manifest_xml(variant)
    if not os.path.isdir(MANIFEST_DIR):
        os.makedirs(MANIFEST_DIR)
    main_path = os.path.join(MANIFEST_DIR, variant + ".manifest")
    with open(main_path, "w", encoding="utf-8") as f:
        f.write(xml)
    written = [main_path]
    for name, text in side.items():
        p = os.path.join(APP_DIR, name)
        with open(p, "w", encoding="utf-8") as f:
            f.write(text)
        written.append(p)
    return main_path, written


def activate_manifest(path, assembly_dir):
    ctx = ACTCTXW()
    ctx.cbSize = ctypes.sizeof(ACTCTXW)
    ctx.dwFlags = ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID
    ctx.lpSource = path
    ctx.lpAssemblyDirectory = assembly_dir
    h = kernel32.CreateActCtxW(ctypes.byref(ctx))
    if h == INVALID_HANDLE_VALUE or not h:
        err = ctypes.get_last_error()
        print("RESULT CreateActCtxW                                    FAILED  GetLastError=%d (0x%08X)" % (err, err))
        print("       path=%s" % path)
        return None, None
    print("RESULT CreateActCtxW                                    -> handle 0x%X" % h)
    cookie = ctypes.c_size_t(0)
    ok = kernel32.ActivateActCtx(h, ctypes.byref(cookie))
    if not ok:
        err = ctypes.get_last_error()
        print("RESULT ActivateActCtx                                   FAILED  GetLastError=%d" % err)
        return h, None
    print("RESULT ActivateActCtx                                   -> cookie 0x%X" % cookie.value)
    return h, cookie.value


# ----------------------------------------------------------------------------
# the layers
# ----------------------------------------------------------------------------
def layer_cocreate():
    banner("LAYER 1: ole32!CoCreateInstance(CLSID_TextService, IID_IUnknown)")
    g_clsid = guid(CLSID_TEXTSERVICE)
    g_iid = guid(IID_IUNKNOWN)
    out = ctypes.c_void_p(None)
    hr = ole32.CoCreateInstance(ctypes.byref(g_clsid), None, CLSCTX_INPROC_SERVER,
                                ctypes.byref(g_iid), ctypes.byref(out))
    report("CoCreateInstance(CLSID, IID_IUnknown)", hr)
    if u32(hr) >= 0x80000000 or not out.value:
        return None
    print("OK: IUnknown* = 0x%X" % out.value)
    return out.value


def layer_qi(p):
    banner("LAYER 2: QueryInterface for the TSF IIDs")
    res = {}
    for label, iid in (
        ("ITfTextInputProcessorEx {6E4E2102-F9CD-433D-B496-303CE03A6507}", IID_ITfTextInputProcessorEx),
        ("ITfKeyEventSink         {AA80E7F5-2021-11D2-93E0-0060B067B86E}", IID_ITfKeyEventSink),
        ("ITfTextInputProcessor   {AA80E7F7-2021-11D2-93E0-0060B067B86E}", IID_ITfTextInputProcessor),
        ("IID_IUnknown (idempotent re-QI)", IID_IUNKNOWN),
    ):
        hr, ptr = QI(p, iid)
        report("QI " + label, hr)
        if u32(hr) < 0x80000000 and ptr:
            print("OK: %s -> 0x%X" % (label.split()[0], ptr))
            res[label.split()[0]] = ptr
    return res


k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.GetModuleHandleExW.argtypes = [ctypes.c_uint32, ctypes.c_void_p,
                                   ctypes.POINTER(ctypes.c_void_p)]
k32.GetModuleHandleExW.restype = ctypes.c_uint32
k32.GetModuleFileNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_uint32]
k32.GetModuleFileNameW.restype = ctypes.c_uint32


def module_of(addr):
    """Name of the loaded image containing `addr`, or None if it is in no image
    (i.e. it lives in the CLR's JIT thunk heap, which is not backed by a file)."""
    h = ctypes.c_void_p()
    # 0x4 = FROM_ADDRESS, 0x2 = UNCHANGED_REFCOUNT
    if not k32.GetModuleHandleExW(0x4 | 0x2, ctypes.c_void_p(addr), ctypes.byref(h)):
        return None
    buf = ctypes.create_unicode_buffer(1024)
    k32.GetModuleFileNameW(h, buf, 1024)
    return buf.value.rsplit("\\", 1)[-1]


def safe_call(label, target, index, argtypes, argv=None):
    """Call a vtable slot, but survive a bad one.

    A garbage slot faults the whole process, which would hide every later result.
    So the pointer is classified BEFORE it is called. The CLR writes the
    interface's method thunks contiguously right after the this-pointer block and
    terminates the array with a pointer into clr.dll; JIT thunks themselves live
    in a heap that belongs to no module. So:

      * a pointer outside user space      -> garbage, never call
      * a pointer that lands in clr.dll   -> the array terminator, never call
      * otherwise                         -> a method thunk, safe to call

    `argv` supplies the real argument values, one per entry of `argtypes`. It
    defaults to zeroes, which is right for the pure probe calls (Activate(NULL),
    Deactivate(), OnSetFocus(FALSE)) but WRONG for anything that must receive a
    live pointer -- passing 0 there would silently turn a real activation call
    into a NULL-argument probe and report the callee's E_INVALIDARG guard as if
    it were a defect in the text service.

    Returns (called, hr_or_None, note).
    """
    fnp = slot(target, index)
    raw = fnp if isinstance(fnp, int) else 0
    if raw == 0:
        return False, None, "slot is NULL"
    if raw > 0x00007FFFFFFFFFFF:
        return False, None, ("slot holds 0x%016X -- NOT a callable address, "
                             "refusing to call (a real caller takes an access violation here)"
                             % raw)
    mod = module_of(raw)
    if mod and (mod.lower().startswith("clr") or mod.lower().startswith("mscorwks")):
        return False, None, ("slot holds 0x%016X, which is inside %s -- that is the CLR's "
                             "end-of-array terminator, not a method (a real caller takes an "
                             "access violation here)" % (raw, mod))
    try:
        # the `this` pointer is the first parameter of every COM method
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, *argtypes)(raw)
        args = [target] + (list(argv) if argv is not None else [0] * len(argtypes))
        hr = fn(*args)
        return True, hr, None
    except OSError as ex:
        return False, None, "faulted: %s" % ex


def layer_calls(p, ptrs):
    banner("LAYER 3: real method calls through the vtables")

    pTip = ptrs.get("ITfTextInputProcessor")
    pEx = ptrs.get("ITfTextInputProcessorEx")
    pKey = ptrs.get("ITfKeyEventSink")

    # ---- safe probes first: these behave identically on a correct and a broken
    # build, and they prove the managed bodies really run in THIS native process.
    if pTip:
        ok, hr, note = safe_call("ITfTextInputProcessor::Activate(NULL, 0)", pTip, 3,
                                 (ctypes.c_void_p, ctypes.c_uint32))
        if ok:
            report("call ITfTextInputProcessor::Activate(NULL, 0)", hr,
                   expected_fail=HResult_E_INVALIDARG)
            print("     TextService.cs:181 has `if (ptim == null) return HResult.E_INVALIDARG;`")
            print("     -> 0x80070057 proves the MANAGED BODY executed in this process.")
        else:
            print("RESULT call ITfTextInputProcessor::Activate(NULL, 0)   SKIPPED: %s" % note)

    if pTip:
        ok, hr, note = safe_call("ITfTextInputProcessor::Deactivate()", pTip, 4, ())
        if ok:
            report("call ITfTextInputProcessor::Deactivate()", hr)
        else:
            print("RESULT call ITfTextInputProcessor::Deactivate()   SKIPPED: %s" % note)

    if pKey:
        ok, hr, note = safe_call("ITfKeyEventSink::OnSetFocus(FALSE)", pKey, 3, (ctypes.c_int,))
        if ok:
            report("call ITfKeyEventSink::OnSetFocus(FALSE)", hr)
        else:
            print("RESULT call ITfKeyEventSink::OnSetFocus(FALSE)   SKIPPED: %s" % note)

    # ---- the probes that distinguish a correct CCW from the shipped defect.
    # TSF activates through ITfTextInputProcessorEx (ActivateEx = slot 5), falling
    # back to ITfTextInputProcessor only if the Ex QI fails. So slot 5 is the one
    # that decides whether "selecting the IME" survives.
    banner("LAYER 3b: the TSF activation path -- ITfTextInputProcessorEx")
    for index, what, argtypes, expected in (
        (4, "Deactivate()", (), None),
        (5, "ActivateEx(NULL, 0, 0)", (ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32),
         HResult_E_INVALIDARG),
    ):
        if not pEx:
            print("RESULT IEx slot[%d] %-24s no pointer (QI failed)" % (index, what))
            continue
        raw = slot(pEx, index)
        print("vtable slot[%d] of ITfTextInputProcessorEx = 0x%016X" % (index, raw))
        ok, hr, note = safe_call(what, pEx, index, argtypes)
        if ok:
            report("call ITfTextInputProcessorEx::" + what, hr, expected_fail=expected)
        else:
            print("RESULT call ITfTextInputProcessorEx::%-24s SKIPPED: %s" % (what, note))
            print("     *** This slot is where a real TSF host (or any caller following the")
            print("     *** published COM contract) invokes %s. Executing it faults." % what)
            FAIL_BITS.add("ITfTextInputProcessorEx slot[%d] is not callable" % index)


def layer_realthreadmgr(p):
    banner("LAYER 4: ActivateEx with a REAL ITfThreadMgr produced by TSF")
    g_clsid = guid(CLSID_TF_ThreadMgr)
    g_iid = guid(IID_ITfThreadMgr)
    tm = ctypes.c_void_p(None)
    hr = ole32.CoCreateInstance(ctypes.byref(g_clsid), None, CLSCTX_INPROC_SERVER,
                                ctypes.byref(g_iid), ctypes.byref(tm))
    report("CoCreateInstance(CLSID_TF_ThreadMgr, IID_ITfThreadMgr)", hr)
    if u32(hr) >= 0x80000000 or not tm.value:
        print("NOTE: TSF thread manager unavailable in this process; layer 4 skipped.")
        return
    tm_p = tm.value
    tid = ctypes.c_uint32(0)

    hr2, tmex = QI(tm_p, IID_ITfThreadMgrEx)
    if u32(hr2) < 0x80000000 and tmex:
        report("QI ITfThreadMgrEx {3E90ADE3-7594-4CB0-BB58-69628F5F091B}", hr2)
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p,
                                ctypes.POINTER(ctypes.c_uint32), ctypes.c_uint32)(slot(tmex, 3))
        hr = fn(tmex, ctypes.byref(tid), 0)
        report("ITfThreadMgrEx::ActivateEx(&tid, 0)", hr)
    else:
        # ITfThreadMgrEx is an optional newer interface; the thread manager in this
        # process not exposing it is NOT a defect. Report it informationally and fall
        # back to the plain ITfThreadMgr activation, which is all layer 4 needs.
        print("RESULT QI ITfThreadMgrEx {3E90ADE3-7594-4CB0-BB58-69628F5F091B}   "
              "hr=0x%08X  (optional interface absent, falling back)" % u32(hr2))
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p,
                                ctypes.POINTER(ctypes.c_uint32))(slot(tm_p, 3))
        hr = fn(tm_p, ctypes.byref(tid))
        report("ITfThreadMgr::Activate(&tid)", hr)
    print("     tid = %d" % tid.value)

    if tid.value:
        hr3, pEx = QI(p, IID_ITfTextInputProcessorEx)
        if u32(hr3) < 0x80000000 and pEx:
            # This is THE call a real TSF host makes when the user picks the IME in
            # the language bar. Classified before it is made, so a broken CCW is
            # reported instead of taking this whole process down.
            #
            # argv carries the LIVE thread manager pointer. Only the NULL probe may
            # pass 0 here; passing 0 for a real activation is what E_INVALIDARG means.
            ok, hr, note = safe_call("ActivateEx(real ITfThreadMgr, tid, 0)", pEx, 5,
                                     (ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32),
                                     argv=(ctypes.c_void_p(tm_p), tid.value, 0))
            if ok:
                report("TextService::ActivateEx(real ITfThreadMgr, tid, 0)", hr)
                if u32(hr) == 0x80070057:
                    print("     *** 0x80070057 from a NON-NULL thread manager means the text")
                    print("     *** service received no usable ITfThreadMgr: the real host")
                    print("     *** interprets this as 'activation failed' and the IME")
                    print("     *** silently does nothing after being selected.")
            else:
                print("RESULT TextService::ActivateEx(real ITfThreadMgr, tid, 0)")
                print("       SKIPPED: %s" % note)
                print("       *** THIS IS THE REAL FAILURE MODE OF THE SHIPPED BUILD: TSF's")
                print("       *** activation call lands on this slot, so selecting the IME")
                print("       *** in the input-method list kills the host process instead")
                print("       *** of activating a text service.")
                FAIL_BITS.add("ActivateEx slot is not callable -> host process dies on IME selection")

            ok2, hr2, note2 = safe_call("Deactivate() after real ActivateEx", pEx, 4, ())
            if ok2:
                report("TextService::Deactivate() after real ActivateEx", hr2)
            else:
                print("RESULT TextService::Deactivate() after real ActivateEx")
                print("       SKIPPED: %s" % note2)
            Release(pEx)

    fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)(slot(tm_p, 4))
    hr = fn(tm_p)
    report("ITfThreadMgr::Deactivate()", hr)
    Release(tm_p)


# ----------------------------------------------------------------------------
# fallback paths
# ----------------------------------------------------------------------------
def path_mscoree():
    banner("FALLBACK A: LoadLibrary(mscoree.dll) + DllGetClassObject")
    try:
        md = ctypes.WinDLL("mscoree.dll")
    except OSError as ex:
        print("RESULT LoadLibrary(mscoree.dll)                         FAILED  %s" % ex)
        return
    print("RESULT LoadLibrary(mscoree.dll)                         -> 0x%X" % (md._handle or 0))
    try:
        fn = md.DllGetClassObject
    except AttributeError:
        print("RESULT GetProcAddress(mscoree!DllGetClassObject)        FAILED  (export not found)")
        return
    print("RESULT GetProcAddress(mscoree!DllGetClassObject)        -> 0x%X" % ctypes.cast(fn, ctypes.c_void_p).value)

    fn.argtypes = [ctypes.POINTER(GUID), ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p)]
    fn.restype = ctypes.c_int32

    g_clsid = guid(CLSID_TEXTSERVICE)
    g_iid = guid(IID_ICLASSFACTORY)
    out = ctypes.c_void_p(None)
    hr = fn(ctypes.byref(g_clsid), ctypes.byref(g_iid), ctypes.byref(out))
    report("mscoree!DllGetClassObject(CLSID, IID_IClassFactory)", hr)
    if u32(hr) < 0x80000000 and out.value:
        print("OK: IClassFactory* = 0x%X  <-- mscoree resolved the CLSID somehow!" % out.value)
        fnc = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.c_void_p,
                                 ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))(slot(out.value, 3))
        g_iid2 = guid(IID_IUNKNOWN)
        obj = ctypes.c_void_p(None)
        hr2 = fnc(out.value, None, ctypes.byref(g_iid2), ctypes.byref(obj))
        report("IClassFactory::CreateInstance(NULL, IID_IUnknown)", hr2)
        if u32(hr2) < 0x80000000 and obj.value:
            print("OK: IUnknown* = 0x%X" % obj.value)
    else:
        print("EXPECTED: mscoree looks the CLSID up in HKCR\\CLSID\\{...}\\InprocServer32\\{Assembly,Class},")
        print("          which we deliberately never wrote.  Registration-free manifests are")
        print("          consumed by ole32's CoCreateInstance, NOT by mscoree's DllGetClassObject.")


def path_clrcreateinstance():
    banner("FALLBACK B: CLRCreateInstance -> ICLRMetaHost -> ICLRRuntimeInfo")
    try:
        mh = ctypes.WinDLL("mscoree.dll")
        fn = mh.CLRCreateInstance
    except (OSError, AttributeError) as ex:
        print("RESULT CLRCreateInstance unavailable: %s" % ex)
        return
    fn.argtypes = [ctypes.POINTER(GUID), ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p)]
    fn.restype = ctypes.c_int32
    g_clsid = guid(CLSID_CLRMetaHost)
    g_iid = guid(IID_ICLRMetaHost)
    metahost = ctypes.c_void_p(None)
    hr = fn(ctypes.byref(g_clsid), ctypes.byref(g_iid), ctypes.byref(metahost))
    report("CLRCreateInstance(CLSID_CLRMetaHost, IID_ICLRMetaHost)", hr)
    if u32(hr) >= 0x80000000 or not metahost.value:
        return

    # ICLRMetaHost layout:
    #   0 QI, 1 AddRef, 2 Release,
    #   3 GetRuntime(LPCWSTR, REFIID, void**)
    fn_get = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, wintypes.LPCWSTR,
                                ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))(slot(metahost.value, 3))
    g_iid_ri = guid(IID_ICLRRuntimeInfo)
    ri = ctypes.c_void_p(None)
    hr = fn_get(metahost.value, u"v4.0.30319", ctypes.byref(g_iid_ri), ctypes.byref(ri))
    report('ICLRMetaHost::GetRuntime(L"v4.0.30319", IID_ICLRRuntimeInfo)', hr)
    if u32(hr) < 0x80000000 and ri.value:
        print("OK: ICLRRuntimeInfo* = 0x%X" % ri.value)
        # ICLRRuntimeInfo layout: 0 QI, 1 AddRef, 2 Release,
        #   3 GetVersionString, 4 GetRuntimeDirectory, 5 IsLoaded, 6 LoadErrorString,
        #   7 LoadLibrary, 8 GetProcAddress, 9 GetInterface
        buf = ctypes.create_unicode_buffer(64)
        fn_ver = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, wintypes.LPWSTR,
                                    ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32))(slot(ri.value, 3))
        n = ctypes.c_uint32(0)
        hr = fn_ver(ri.value, buf, 64, ctypes.byref(n))
        report("ICLRRuntimeInfo::GetVersionString", hr)
        if u32(hr) < 0x80000000:
            print("     runtime version = %s" % buf.value)

        dll = ctypes.create_unicode_buffer(520)
        fn_dir = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, wintypes.LPWSTR,
                                    ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32))(slot(ri.value, 4))
        hr = fn_dir(ri.value, dll, 520, ctypes.byref(n))
        report("ICLRRuntimeInfo::GetRuntimeDirectory", hr)
        if u32(hr) < 0x80000000:
            print("     runtime dir     = %s" % dll.value)

        # ICLRRuntimeHost lives at the IID below
        g_iid_host = guid(IID_ICLRRuntimeHost)
        host = ctypes.c_void_p(None)
        fn_gi = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.POINTER(GUID),
                                   ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))(slot(ri.value, 9))
        g_clsid_host = guid(CLSID_CLRRuntimeHost)
        hr = fn_gi(ri.value, ctypes.byref(g_clsid_host), ctypes.byref(g_iid_host), ctypes.byref(host))
        report("ICLRRuntimeInfo::GetInterface(CLSID_CLRRuntimeHost, IID_ICLRRuntimeHost)", hr)
        if u32(hr) < 0x80000000 and host.value:
            print("OK: ICLRRuntimeHost* = 0x%X" % host.value)
            # ICLRRuntimeHost layout: 0 QI, 1 AddRef, 2 Release, 3 Start, 4 Stop, ...
            fn_start = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)(slot(host.value, 3))
            hr = fn_start(host.value)
            report("ICLRRuntimeHost::Start()  (loads the CLR into this native process)", hr)
            fn_stop = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)(slot(host.value, 4))
            hr = fn_stop(host.value)
            report("ICLRRuntimeHost::Stop()", hr)


# ----------------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--variant", required=True)
    ap.add_argument("--no-manifest", action="store_true")
    ap.add_argument("--appdir", default=None,
                    help="directory the activation context treats as the assembly "
                         "directory (default: ..\\app). Use it to point the same "
                         "manifest at a different build of Xingbian.Tsf.dll.")
    args = ap.parse_args()

    global APP_DIR
    if args.appdir:
        APP_DIR = os.path.abspath(args.appdir)

    print("=" * 78)
    print("ts_host.py (PYTHON_NATIVE)  variant=%s" % args.variant)
    print("interpreter  : %s" % sys.executable)
    print("pid          : %d" % os.getpid())
    print("bits         : %d" % (ctypes.sizeof(ctypes.c_void_p) * 8))
    print("app dir      : %s" % APP_DIR)
    print("=" * 78)

    hr = ole32.CoInitializeEx(None, 2)  # COINIT_APARTMENTTHREADED
    report("CoInitializeEx(APARTMENTTHREADED)", hr)

    if args.variant in ("baseline_nomanifest", "registry_route"):
        banner("REGISTRY ROUTE: no activation context, resolve through HKLM\\SOFTWARE\\Classes")
        print("Nothing is passed to CreateActCtxW -- ole32!CoCreateInstance is left to")
        print("resolve the CLSID the ordinary way, i.e. through the InprocServer32 key.")
        print("")
        print("The key this run depends on:")
        print("  HKLM\\SOFTWARE\\Classes\\CLSID\\{%s}" % CLSID_BRACED.strip("{}"))
        print("    (default)      = C:\\Windows\\System32\\mscoree.dll")
        print("    ThreadingModel = Both")
        print("    Assembly       = Xingbian.Tsf, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null")
        print("    Class          = Xingbian.Tsf.TextService")
        print("    RuntimeVersion = v4.0.30319")
        print("    CodeBase       = file:///C:/Program Files/XingbianIME/Xingbian.Tsf.dll")
        print("")
        print(">>> If this printed REGDB_E_CLASSNOTREG, the CLSID is genuinely unregistered")
        print(">>> on this machine -- so anything that works later works *because of the")
        print(">>> manifest*, not because of a leftover registry entry.")
        p = layer_cocreate()
        if p:
            ptrs = layer_qi(p)
            layer_calls(p, ptrs)
            layer_realthreadmgr(p)
            banner("CLEANUP")
            rc = Release(p)
            print("RESULT final IUnknown::Release                          -> refcount %u" % rc)
        else:
            print("")
            print(">>> CoCreateInstance failed; the managed object was never created.")
    elif args.variant == "mscoree":
        path_mscoree()
    elif args.variant == "clrcreateinstance":
        path_clrcreateinstance()
    else:
        path, written = write_manifests(args.variant)
        banner("MANIFEST")
        print("main manifest : %s" % path)
        for w in written:
            print("written       : %s" % w)
        with open(path, "r", encoding="utf-8") as f:
            for line in f.read().splitlines():
                print("   | " + line)

        if not args.no_manifest:
            h, cookie = activate_manifest(path, APP_DIR)
            if h is None:
                print("")
                print(">>> CreateActCtxW refused the manifest -- the SxS parser rejected it.")
                print(">>> This is itself a result: the element/attribute combination is invalid.")
        else:
            banner("MANIFEST NOT ACTIVATED (--no-manifest)")

        p = layer_cocreate()
        if p:
            ptrs = layer_qi(p)
            layer_calls(p, ptrs)
            layer_realthreadmgr(p)
            banner("CLEANUP")
            rc = Release(p)
            print("RESULT final IUnknown::Release                          -> refcount %u" % rc)
        else:
            print("")
            print(">>> CoCreateInstance failed; the managed object was never created.")

    print("")
    print("=" * 78)
    if EXPECTED_BITS:
        print("NULL-PROBE GUARDS THAT FIRED AS DESIGNED (%d): %s"
              % (len(EXPECTED_BITS), "; ".join(sorted(EXPECTED_BITS))))
        print("    Each of these was called with a NULL argument on purpose; the text")
        print("    service's own null check returning E_INVALIDARG is the PROOF that its")
        print("    managed method body executed inside this native process.")
    if FAIL_BITS:
        print("FAILED STEPS (%d): %s" % (len(FAIL_BITS), "; ".join(sorted(FAIL_BITS))))
    else:
        print("ALL STEPS SUCCEEDED")
    print("=" * 78)


if __name__ == "__main__":
    main()
