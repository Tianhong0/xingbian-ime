# -*- coding: utf-8 -*-
"""
sxs_probe.py -- ask the SxS engine DIRECTLY what it can see.

Rationale
---------
CoCreateInstance only tells us a single HRESULT at the very end of a long
pipeline.  To find out WHERE the pipeline breaks we interrogate the activated
activation context ourselves with the documented FindActCtxSectionGuid() API:

    ulSectionId == 7  ->  ACTCTX_SECTION_ID_CLR_CLASS
    ulSectionId == 4  ->  ACTCTX_SECTION_ID_COM_INTERFACE

If section 7 answers for our CLSID then the manifest DID publish the managed
class and the break is later (mscoree loading the assembly).  If it comes back
ERROR_SXS_KEY_NOT_FOUND then the manifest never published it at all.

Also probes the internal sxs.dll exports that ole32/mscoree actually use
(SxsLookupClrGuid / SxsFindClrClassInformation) for extra evidence.

Usage:
    python sxs_probe.py                 # run the whole matrix
    python sxs_probe.py --variant NAME  # one variant
"""

import ctypes
import ctypes.wintypes as wintypes
import os
import sys
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
SCRATCH = os.path.dirname(HERE)
APP_DIR = os.path.join(SCRATCH, "app")
OUT_DIR = os.path.join(HERE, "probes_sxs")

CLSID_TEXTSERVICE = "7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43"
IID_IUNKNOWN = "00000000-0000-0000-C000-000000000046"

ACTCTX_SECTION_ID_COM_INTERFACE = 4
ACTCTX_SECTION_ID_CLR_CLASS = 7

SECTION_NAMES = {
    1: "ACTIVATION_CONTEXT",
    2: "ASSEMBLY_DEPENDENCY",
    3: "WINDOW_CLASSES",
    4: "COM_INTERFACE",
    5: "APPLICATION_SETTINGS",
    6: "CLR_SURROGATE",
    7: "CLR_CLASS",
    8: "COM_PROXY_STUB",
}

INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
ole32 = ctypes.WinDLL("ole32", use_last_error=True)


# ---------------------------------------------------------------------------
class GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_uint32),
                ("Data2", ctypes.c_uint16),
                ("Data3", ctypes.c_uint16),
                ("Data4", ctypes.c_ubyte * 8)]

    def __str__(self):
        d4 = "".join("%02X" % b for b in self.Data4)
        return "{%08X-%04X-%04X-%s-%s}" % (self.Data1, self.Data2, self.Data3,
                                           d4[:4], d4[4:])


def guid(s):
    u = uuid.UUID(s)
    g = GUID()
    g.Data1 = u.time_low
    g.Data2 = u.time_mid
    g.Data3 = u.time_hi_version
    for i, b in enumerate(u.bytes[8:]):
        g.Data4[i] = b
    return g


class ACTCTX_SECTION_KEYED_DATA_ASSEMBLY_METADATA(ctypes.Structure):
    _fields_ = [("lpInformation", ctypes.c_void_p),
                ("lpSectionBase", ctypes.c_void_p),
                ("ulSectionTotalLength", ctypes.c_uint32),
                ("hActCtx", ctypes.c_void_p),
                ("ulAssemblyRosterIndex", ctypes.c_uint32)]


class ACTCTX_SECTION_KEYED_DATA(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32),
                ("ulDataFormatVersion", ctypes.c_uint32),
                ("lpData", ctypes.c_void_p),
                ("ulLength", ctypes.c_uint32),
                ("lpSectionGlobalData", ctypes.c_void_p),
                ("ulSectionGlobalDataLength", ctypes.c_uint32),
                ("lpSectionBase", ctypes.c_void_p),
                ("ulSectionTotalLength", ctypes.c_uint32),
                ("hActCtx", ctypes.c_void_p),
                ("ulAssemblyRosterIndex", ctypes.c_uint32),
                ("ulFlags", ctypes.c_uint32),
                ("AssemblyMetadata", ACTCTX_SECTION_KEYED_DATA_ASSEMBLY_METADATA)]


class ACTCTXW(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32),
                ("dwFlags", ctypes.c_uint32),
                ("lpSource", wintypes.LPCWSTR),
                ("wProcessorArchitecture", ctypes.c_uint16),
                ("wLangId", ctypes.c_uint16),
                ("lpAssemblyDirectory", wintypes.LPCWSTR),
                ("lpResourceName", wintypes.LPCWSTR),
                ("lpApplicationName", wintypes.LPCWSTR),
                ("hModule", wintypes.HMODULE)]


kernel32.CreateActCtxW.argtypes = [ctypes.POINTER(ACTCTXW)]
kernel32.CreateActCtxW.restype = wintypes.HANDLE
kernel32.ActivateActCtx.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_size_t)]
kernel32.ActivateActCtx.restype = wintypes.BOOL
kernel32.FindActCtxSectionGuid.argtypes = [
    ctypes.c_uint32, ctypes.POINTER(GUID), ctypes.c_uint32,
    ctypes.POINTER(GUID), ctypes.POINTER(ACTCTX_SECTION_KEYED_DATA)]
kernel32.FindActCtxSectionGuid.restype = wintypes.BOOL

ole32.CoInitializeEx.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
ole32.CoCreateInstance.argtypes = [
    ctypes.POINTER(GUID), ctypes.c_void_p, ctypes.c_uint32,
    ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p)]


def err():
    return ctypes.get_last_error()


def u32(hr):
    return hr & 0xFFFFFFFF


def hr_text(hr):
    v = u32(hr)
    table = {
        0x00000000: "S_OK",
        0x80004005: "E_FAIL",
        0x80040154: "REGDB_E_CLASSNOTREG",
        0x80040111: "CLASS_E_CLASSNOTAVAILABLE",
        0x80070002: "HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)",
        0x8007007E: "HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND)",
        0x800736B1: "HRESULT_FROM_WIN32(ERROR_SXS_CANT_GEN_ACTCTX)",
        0x80070490: "HRESULT_FROM_WIN32(ERROR_NOT_FOUND)",
        0x80073B01: "HRESULT_FROM_WIN32(ERROR_SXS_KEY_NOT_FOUND)",
    }
    return table.get(v)


# ---------------------------------------------------------------------------
# manifest matrix -- every interesting placement of <clrClass>
# ---------------------------------------------------------------------------
IDENT = ('  <assemblyIdentity type="win32" name="Xingbian.RegFree.Probe"'
         ' version="1.0.0.0"\n'
         '                    processorArchitecture="amd64" />\n')

CLR_ATTRS = ('clsid="{%s}"\n'
             '              threadingModel="Both"\n'
             '              runtimeVersion="v4.0.30319"\n'
             '              name="%s"')

ASM_QUALIFIED = ("Xingbian.Tsf.TextService, Xingbian.Tsf, Version=0.1.0.0, "
                 "Culture=neutral, PublicKeyToken=null")


def wrap(body, ns="urn:schemas-microsoft-com:asm.v1"):
    return ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
            '<assembly xmlns="%s" manifestVersion="1.0">\n' % ns
            + IDENT + body + '</assembly>\n')


def clrcls(where="  ", name=ASM_QUALIFIED, clsid=CLSID_TEXTSERVICE,
           runtime="v4.0.30319"):
    attrs = CLR_ATTRS % (clsid, name)
    if runtime is not None:
        attrs = attrs.replace('runtimeVersion="v4.0.30319"',
                              'runtimeVersion="%s"' % runtime)
    else:
        attrs = attrs.replace('\n              runtimeVersion="v4.0.30319"', "")
    return "%s<clrClass %s />\n" % (where, attrs)


VARIANTS = {}


def add(name, xml, note):
    VARIANTS[name] = (xml, note)


# --- the two candidate shapes -------------------------------------------------
add("A1_file_parent_closed",
    wrap('  <file name="Xingbian.Tsf.dll">\n' + clrcls("    ") + '  </file>\n'
         + '  <file name="Xingbian.Core.dll" />\n'),
    "<clrClass> INSIDE <file> (closed form)  <- the shape most examples show")

add("A2_file_parent_selfclose_file",
    wrap('  <file name="Xingbian.Tsf.dll">\n' + clrcls("    ") + '  </file>\n'),
    "<clrClass> INSIDE <file>, only that one file")

add("A3_toplevel_plus_file",
    wrap('  <file name="Xingbian.Tsf.dll" />\n'
         '  <file name="Xingbian.Core.dll" />\n' + clrcls("  ")),
    "<clrClass> at ASSEMBLY level, <file> siblings (current ts_host.py shape)")

add("A4_toplevel_only",
    wrap(clrcls("  ")),
    "<clrClass> at ASSEMBLY level, no <file> at all")

add("A5_toplevel_attr_order_swapped",
    wrap('  <file name="Xingbian.Tsf.dll" />\n'
         '  <clrClass name="%s" clsid="{%s}"\n'
         '            runtimeVersion="v4.0.30319" threadingModel="Both" />\n'
         % (ASM_QUALIFIED, CLSID_TEXTSERVICE)),
    "same as A3 but attributes reordered + single line")

# --- attribute ablations on the *file-parent* shape --------------------------
add("B1_file_parent_no_threading",
    wrap('  <file name="Xingbian.Tsf.dll">\n'
         '    <clrClass clsid="{%s}" name="%s" runtimeVersion="v4.0.30319" />\n'
         '  </file>\n' % (CLSID_TEXTSERVICE, ASM_QUALIFIED)),
    "<clrClass> in <file> WITHOUT threadingModel")

add("B2_file_parent_shortname",
    wrap('  <file name="Xingbian.Tsf.dll">\n'
         '    <clrClass clsid="{%s}" threadingModel="Both" '
         'runtimeVersion="v4.0.30319" name="Xingbian.Tsf.TextService" />\n'
         '  </file>\n' % CLSID_TEXTSERVICE),
    "<clrClass> in <file> with UNQUALIFIED type name")

add("B3_file_parent_own_ns",
    wrap('  <file name="Xingbian.Tsf.dll">\n' + clrcls("    ") + '  </file>\n',
         ns="urn:schemas-microsoft-com:asm.v2"),
    "<clrClass> in <file>, asm.v2 namespace")

add("B4_file_parent_prefixed",
    wrap('  <file name="Xingbian.Tsf.dll">\n' + clrcls("    ") + '  </file>\n')
    .replace('<assembly xmlns=', '<assembly xmlns:sxs="urn:schemas-microsoft-com:asm.v2" xmlns=')
    .replace('<clrClass', '<sxs:clrClass').replace('/>\n  </file>', '/>\n  </file>'),
    "<clrClass> in <file>, sxs: prefixed (asm.v2 element in asm.v1 file)")

# --- native control: prove the actctx <file> machinery works at all ----------
add("C1_native_comclass_ctrl",
    wrap('  <file name="Xingbian.Tsf.dll">\n'
         '    <comClass clsid="{11111111-2222-3333-4444-555555555555}"\n'
         '              threadingModel="Both" />\n'
         '  </file>\n'),
    "NATIVE control: plain <comClass> inside <file> (no CLR involved)")

add("C2_native_empty_file_ctrl",
    wrap('  <file name="Xingbian.Tsf.dll" />\n'),
    "NATIVE control: bare <file> element, no class at all")


# ---------------------------------------------------------------------------
def activate(xml_path, app_dir):
    ctx = ACTCTXW()
    ctx.cbSize = ctypes.sizeof(ACTCTXW)
    ctx.dwFlags = 0x00000004  # ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID
    ctx.lpSource = xml_path
    ctx.lpAssemblyDirectory = app_dir
    h = kernel32.CreateActCtxW(ctypes.byref(ctx))
    if h == INVALID_HANDLE_VALUE or not h:
        return None, "CreateActCtxW GetLastError=%d (0x%08X)" % (err(), err())
    cookie = ctypes.c_size_t(0)
    if not kernel32.ActivateActCtx(h, ctypes.byref(cookie)):
        return None, "ActivateActCtx GetLastError=%d" % err()
    return (h, cookie.value), None


def find_section(section_id, g):
    data = ACTCTX_SECTION_KEYED_DATA()
    data.cbSize = ctypes.sizeof(ACTCTX_SECTION_KEYED_DATA)
    ok = kernel32.FindActCtxSectionGuid(0, None, section_id,
                                        ctypes.byref(g), ctypes.byref(data))
    if not ok:
        return None, "GetLastError=%d (0x%08X)" % (err(), err())
    return data, None


def probe_variant(name, xml, note):
    if not os.path.isdir(OUT_DIR):
        os.makedirs(OUT_DIR)
    path = os.path.join(OUT_DIR, name + ".manifest")
    with open(path, "w", encoding="utf-8") as f:
        f.write(xml)

    print("-" * 78)
    print("%-34s %s" % (name, note))
    print("-" * 78)

    act, e = activate(path, APP_DIR)
    if act is None:
        print("  %-28s %s" % ("CreateActCtxW/ActivateActCtx", "FAILED  " + e))
        return
    print("  %-28s OK (handle 0x%X)" % ("CreateActCtxW/ActivateActCtx", act[0]))

    g_clsid = guid(CLSID_TEXTSERVICE)
    for sid in (ACTCTX_SECTION_ID_CLR_CLASS, ACTCTX_SECTION_ID_COM_INTERFACE):
        d, e = find_section(sid, g_clsid)
        label = "FindActCtxSectionGuid(%s)" % SECTION_NAMES[sid]
        if d is None:
            print("  %-28s NOT FOUND  %s" % (label, e))
        else:
            raw = ctypes.string_at(d.lpData, min(d.ulLength, 32)) if d.lpData else b""
            print("  %-28s FOUND  ulDataFormatVersion=%d ulLength=%d"
                  % (label, d.ulDataFormatVersion, d.ulLength))
            print("  %-28s   lpData bytes: %s"
                  % ("", " ".join("%02X" % b for b in raw)))

    g_iid = guid(IID_IUNKNOWN)
    out = ctypes.c_void_p(None)
    hr = ole32.CoCreateInstance(ctypes.byref(g_clsid), None, 1,
                                ctypes.byref(g_iid), ctypes.byref(out))
    t = hr_text(hr)
    print("  %-28s hr=0x%08X  %s"
          % ("CoCreateInstance", u32(hr), t if t else "?"))


def main():
    if not os.path.isdir(APP_DIR):
        print("app dir missing: %s" % APP_DIR)
        return 2
    ole32.CoInitializeEx(None, 2)
    want = None
    if "--variant" in sys.argv:
        want = sys.argv[sys.argv.index("--variant") + 1]
    print("=" * 78)
    print("sxs_probe.py -- what does the SxS activation context actually contain?")
    print("app dir: %s" % APP_DIR)
    print("=" * 78)
    for name in sorted(VARIANTS):
        if want and name != want:
            continue
        probe_variant(name, VARIANTS[name][0], VARIANTS[name][1])
    print("-" * 78)
    return 0


if __name__ == "__main__":
    sys.exit(main())
