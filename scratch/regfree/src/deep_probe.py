# -*- coding: utf-8 -*-
"""
deep_probe.py -- find out exactly WHERE the manifest activation pipeline breaks.

sxs_probe.py established:
  * <clrClass> nested in <file>  -> CreateActCtxW refuses the manifest (14001)
  * <clrClass> at assembly level -> CreateActCtxW ok, but
                                    FindActCtxSectionGuid(CLR_CLASS=7) = NOT FOUND
                                    CoCreateInstance = 0x80070002

This script answers the two follow-up questions:

  Q1  What is actually IN the activation context for our CLSID?
      - dump every GUID-keyed section (1..9), full record, with ASCII strings
  Q2  Does ole32 get as far as loading mscoree.dll before failing?
      - enumerate process modules before and after CoCreateInstance
      If mscoree IS loaded, SxS resolved the class and the CLR is what failed
      with ERROR_FILE_NOT_FOUND.  If it is NOT loaded, SxS/ole32 never even got
      to the CLR.

Usage: python deep_probe.py [--variant NAME]
"""

import ctypes
import ctypes.wintypes as wintypes
import os
import sys
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from sxs_probe import (  # noqa: E402
    APP_DIR, VARIANTS, GUID, guid, u32, hr_text, activate,
    CLSID_TEXTSERVICE, IID_IUNKNOWN, ole32, kernel32,
    ACTCTX_SECTION_KEYED_DATA, INVALID_HANDLE_VALUE,
)

psapi = ctypes.WinDLL("psapi", use_last_error=True)
psapi.EnumProcessModules.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p),
                                     ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32)]
psapi.EnumProcessModules.restype = wintypes.BOOL
psapi.GetModuleBaseNameW.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                     wintypes.LPWSTR, ctypes.c_uint32]
psapi.GetModuleBaseNameW.restype = ctypes.c_uint32

kernel32.FindActCtxSectionGuid.argtypes = [
    ctypes.c_uint32, ctypes.POINTER(GUID), ctypes.c_uint32,
    ctypes.POINTER(GUID), ctypes.POINTER(ACTCTX_SECTION_KEYED_DATA)]
kernel32.FindActCtxSectionGuid.restype = wintypes.BOOL
kernel32.GetCurrentProcess.restype = ctypes.c_void_p

SECTIONS = {
    1: "ACTIVATION_CONTEXT",
    2: "ASSEMBLY_DEPENDENCY",
    3: "WINDOW_CLASSES",
    4: "COM_INTERFACE",
    5: "APPLICATION_SETTINGS",
    6: "CLR_SURROGATE",
    7: "CLR_CLASS",
    8: "COM_PROXY_STUB",
    9: "CLR_CLASS(9?)",
}

# load-bearing exports worth calling by hand
sxs = None
try:
    sxs = ctypes.WinDLL("sxs", use_last_error=True)
except OSError:
    pass


def modules():
    """Return the set of loaded module base names."""
    proc = kernel32.GetCurrentProcess()
    needed = ctypes.c_uint32(0)
    psapi.EnumProcessModules(proc, None, 0, ctypes.byref(needed))
    count = needed.value // ctypes.sizeof(ctypes.c_void_p)
    arr = (ctypes.c_void_p * max(count, 1))()
    if not psapi.EnumProcessModules(proc, arr, ctypes.sizeof(arr), ctypes.byref(needed)):
        return set()
    out = set()
    buf = ctypes.create_unicode_buffer(512)
    for i in range(needed.value // ctypes.sizeof(ctypes.c_void_p)):
        n = psapi.GetModuleBaseNameW(proc, arr[i], buf, 512)
        if n:
            out.add(buf.value.lower())
    return out


def ascii_strings(data):
    out = []
    cur = []
    for b in data:
        if 32 <= b < 127:
            cur.append(chr(b))
        else:
            if len(cur) >= 4:
                out.append("".join(cur))
            cur = []
    if len(cur) >= 4:
        out.append("".join(cur))
    return out


def dump_sections(g):
    for sid in sorted(SECTIONS):
        d = ACTCTX_SECTION_KEYED_DATA()
        d.cbSize = ctypes.sizeof(ACTCTX_SECTION_KEYED_DATA)
        ok = kernel32.FindActCtxSectionGuid(0, None, sid, ctypes.byref(g), ctypes.byref(d))
        label = "section %d %s" % (sid, SECTIONS[sid])
        if not ok:
            print("    %-40s NOT FOUND  err=%d" % (label, ctypes.get_last_error()))
            continue
        raw = ctypes.string_at(d.lpData, d.ulLength) if d.lpData else b""
        strs = ascii_strings(raw)
        print("    %-40s FOUND  fmt=%d len=%d" % (label, d.ulDataFormatVersion, d.ulLength))
        print("        hex[0:64]: %s" % " ".join("%02X" % b for b in raw[:64]))
        if strs:
            print("        ascii    : %s" % " | ".join(strs[:12]))


def try_sxs_lookup_clr_guid(hactx, g_clsid):
    """mscoree/ole32 resolve a <clrClass> through this internal sxs export.

    NOTE: the real signature of this undocumented export is
        BOOL WINAPI SxsLookupClrGuid(DWORD dwFlags, LPGUID pClsid,
                                     PVOID pvBuffer, DWORD cbBuffer,
                                     PDWORD pcbWritten);
    Calling it with a guessed out-pointer signature corrupts the stack and
    fail-fasts the process (0xC0000409 STATUS_STACK_BUFFER_OVERRUN) -- observed.
    We therefore only *report* that it exists; we do not call it.
    """
    print("    --- sxs!SxsLookupClrGuid (existence check only) ---")
    if sxs is None:
        print("        sxs.dll not loadable")
        return
    try:
        fn = sxs.SxsLookupClrGuid
        print("        export present at 0x%X" % ctypes.cast(fn, ctypes.c_void_p).value)
        print("        (not called -- signature is undocumented and a wrong guess crashes)")
    except AttributeError:
        print("        export missing")
        return


def probe(name):
    xml, note = VARIANTS[name]
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "probes_sxs")
    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    path = os.path.join(out_dir, name + ".manifest")
    with open(path, "w", encoding="utf-8") as f:
        f.write(xml)

    print("=" * 78)
    print("VARIANT %s -- %s" % (name, note))
    print("=" * 78)

    act, e = activate(path, APP_DIR)
    if act is None:
        print("  activation refused: %s" % e)
        return
    hactx = act[0]
    g_clsid = guid(CLSID_TEXTSERVICE)

    print("  Q1: what is in the activation context for CLSID %s ?" % CLSID_TEXTSERVICE)
    dump_sections(g_clsid)
    try_sxs_lookup_clr_guid(hactx, g_clsid)

    print("")
    print("  Q2: does ole32 reach mscoree.dll before failing?")
    before = modules()
    print("    mscoree before : %s" % ("LOADED" if "mscoree.dll" in before else "not loaded"))
    print("    clr.dll before : %s" % ("LOADED" if any(m.startswith("clr") and m.endswith(".dll") for m in before) else "not loaded"))

    out = ctypes.c_void_p(None)
    hr = ole32.CoCreateInstance(ctypes.byref(g_clsid), None, 1,
                                ctypes.byref(guid(IID_IUNKNOWN)), ctypes.byref(out))
    t = hr_text(hr)
    print("    CoCreateInstance hr=0x%08X %s" % (u32(hr), t if t else "?"))

    after = modules()
    print("    mscoree after  : %s" % ("LOADED" if "mscoree.dll" in after else "not loaded"))
    new = sorted(after - before)
    print("    NEW modules    : %s" % (", ".join(new) if new else "(none)"))
    if "mscoree.dll" not in after:
        print("    >>> ole32 never loaded mscoree.dll: the pipeline broke in SxS/ole32,")
        print("    >>> BEFORE any CLR code ran.")


def main():
    ole32.CoInitializeEx(None, 2)
    names = sorted(VARIANTS)
    if "--variant" in sys.argv:
        names = [sys.argv[sys.argv.index("--variant") + 1]]
    for n in names:
        probe(n)
    return 0


if __name__ == "__main__":
    sys.exit(main())
