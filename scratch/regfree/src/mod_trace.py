# -*- coding: utf-8 -*-
"""
mod_trace.py -- after the manifest-driven CoCreateInstance fails, find out HOW
FAR the activation got: did ole32 load mscoree?  did mscoree start the CLR
(clr.dll / mscorlib)?  This separates "SxS never engaged the CLR" from
"the CLR started but could not resolve the assembly".
"""

import ctypes
import ctypes.wintypes as wintypes
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ts_host import (  # noqa: E402
    APP_DIR, CLSID_TEXTSERVICE, IID_IUNKNOWN, guid, u32,
    write_manifests, activate_manifest, ole32, CLSCTX_INPROC_SERVER,
)

psapi = ctypes.WinDLL("psapi", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

psapi.EnumProcessModules.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.HMODULE),
                                     ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32)]
psapi.EnumProcessModules.restype = wintypes.BOOL
psapi.GetModuleFileNameExW.argtypes = [wintypes.HANDLE, wintypes.HMODULE,
                                       wintypes.LPWSTR, ctypes.c_uint32]
psapi.GetModuleFileNameExW.restype = ctypes.c_uint32

INTERESTING = ("mscoree", "clr.dll", "mscorlib", "clrjit", "ole32", "sxs.dll",
               "xingbian", "clbcatq", "combase", "python")


def modules():
    h = kernel32.GetCurrentProcess()
    need = ctypes.c_uint32(0)
    arr = (wintypes.HMODULE * 1024)()
    if not psapi.EnumProcessModules(h, arr, ctypes.sizeof(arr), ctypes.byref(need)):
        return []
    n = need.value // ctypes.sizeof(wintypes.HMODULE)
    out = []
    buf = ctypes.create_unicode_buffer(1024)
    for i in range(n):
        if psapi.GetModuleFileNameExW(h, arr[i], buf, 1024):
            out.append(buf.value)
    return out


def dump(tag):
    print("--- loaded modules matching %s (%s) ---" % ("/".join(INTERESTING), tag))
    for m in modules():
        if any(k in os.path.basename(m).lower() for k in INTERESTING):
            print("    %s" % m)


def main():
    variant = sys.argv[1] if len(sys.argv) > 1 else "V1_toplevel_asmqual"
    print("=" * 78)
    print("mod_trace.py -- how far does the manifest-driven activation get?")
    print("variant: %s" % variant)
    print("=" * 78)
    ole32.CoInitializeEx(None, 2)

    path, side = write_manifests(variant)
    print("side manifests written: %s" % (sorted(side) if side else "none"))
    act, _ = activate_manifest(path, APP_DIR)
    print("activation context: %s" % ("OK" if act else "FAILED"))
    dump("BEFORE CoCreateInstance")

    g_clsid, g_iid = guid(CLSID_TEXTSERVICE), guid(IID_IUNKNOWN)
    out = ctypes.c_void_p(None)
    hr = ole32.CoCreateInstance(ctypes.byref(g_clsid), None, CLSCTX_INPROC_SERVER,
                                ctypes.byref(g_iid), ctypes.byref(out))
    print("")
    print("RESULT CoCreateInstance -> hr=0x%08X  ptr=%s" % (u32(hr), hex(out.value or 0)))
    print("")
    dump("AFTER CoCreateInstance")

    # explicit probes: the loader keeps a module loaded even if nobody frees it
    print("")
    for name in ("mscoree.dll", "clr.dll", "mscorlib.ni.dll", "clrjit.dll"):
        h = kernel32.GetModuleHandleW(name)
        print("    GetModuleHandleW(%-16s) = 0x%X" % (name, h or 0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
