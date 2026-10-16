# -*- coding: utf-8 -*-
"""
which_dll.py -- the registry route is green, but WHICH Xingbian.Tsf.dll did the
CLR actually load?

The InprocServer32 key carries

    Assembly = Xingbian.Tsf, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null
    CodeBase = file:///C:/Program Files/XingbianIME/Xingbian.Tsf.dll

and those two values do NOT have equal weight: CodeBase is a *fallback* hint the
loader consults only after normal probing has already failed.  Normal probing
starts at the application base directory, i.e. the directory that holds the host
executable -- and NOT the directory the CLSID key happens to sit in.  This script
proves which one wins by activating through the registry route and then asking
the loader which module owns the CCW's method array.

Usage:  which_dll.py       (whatever interpreter runs it defines the app base)
"""

import ctypes
import ctypes.wintypes as wintypes
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ts_host import (  # noqa: E402
    CLSID_TEXTSERVICE, IID_IUNKNOWN, IID_ITfTextInputProcessorEx,
    guid, u32, ole32, CLSCTX_INPROC_SERVER, QI, Release,
)

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.GetModuleHandleExW.argtypes = [ctypes.c_uint32, ctypes.c_wchar_p,
                                   ctypes.POINTER(ctypes.c_void_p)]
k32.GetModuleHandleExW.restype = wintypes.BOOL
k32.GetModuleHandleW.argtypes = [ctypes.c_wchar_p]
k32.GetModuleHandleW.restype = ctypes.c_void_p
k32.GetModuleFileNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_uint32]
k32.GetModuleFileNameW.restype = ctypes.c_uint32

GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004
GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002

CLR_DLLS = ("mscoree.dll", "clr.dll", "clrjit.dll", "mscorlib.ni.dll")


def path_of_module(h):
    buf = ctypes.create_unicode_buffer(2048)
    if not k32.GetModuleFileNameW(ctypes.c_void_p(h), buf, 2048):
        return None
    return buf.value


def module_for(addr):
    """module that owns `addr`, or None."""
    h = ctypes.c_void_p(None)
    ok = k32.GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
                                | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                ctypes.c_wchar_p(addr), ctypes.byref(h))
    if not ok or not h.value:
        return None
    return path_of_module(h.value)


def slot(p, index):
    vtbl = ctypes.cast(p, ctypes.POINTER(ctypes.c_void_p))[0]
    return ctypes.cast(vtbl, ctypes.POINTER(ctypes.c_void_p))[index]


def main():
    print("=" * 78)
    print("which_dll.py -- registry route: which assembly did the CLR load?")
    print("=" * 78)
    print("host executable : %s" % sys.executable)
    print("app base dir    : %s" % os.path.dirname(os.path.abspath(sys.executable)))
    print("cwd             : %s" % os.getcwd())

    cand = os.path.join(os.path.dirname(os.path.abspath(sys.executable)),
                        "Xingbian.Tsf.dll")
    print("")
    print("--- Xingbian.Tsf.dll in the app base dir? ---")
    print("    %s" % cand)
    print("    -> %s" % ("EXISTS %d B" % os.path.getsize(cand)
                         if os.path.exists(cand) else "absent"))

    hr = ole32.CoInitializeEx(None, 2)
    print("")
    print("RESULT CoInitializeEx  hr=0x%08X" % u32(hr))

    g_clsid, g_iid = guid(CLSID_TEXTSERVICE), guid(IID_IUNKNOWN)
    out = ctypes.c_void_p(None)
    hr = ole32.CoCreateInstance(ctypes.byref(g_clsid), None, CLSCTX_INPROC_SERVER,
                                ctypes.byref(g_iid), ctypes.byref(out))
    print("RESULT CoCreateInstance(CLSID, IID_IUnknown)  hr=0x%08X  ptr=%s"
          % (u32(hr), hex(out.value or 0)))
    if u32(hr) != 0 or not out.value:
        print(">>> activation failed; nothing to trace")
        return 1

    p = out.value
    hr2, qi = QI(p, IID_ITfTextInputProcessorEx)
    print("RESULT QI(ITfTextInputProcessorEx)            hr=0x%08X  ptr=%s"
          % (hr2, hex(qi or 0)))

    print("")
    print("--- vtable as the CLR laid it out for the object the REGISTRY returned ---")
    probe = qi if qi else p
    for i in range(3, 9):
        a = slot(probe, i)
        print("    slot[%d] = 0x%016X   owner=%s"
              % (i, a, module_for(a) or "<no module owns this address>"))

    print("")
    print("--- CLR modules currently loaded ---")
    for name in CLR_DLLS:
        h = k32.GetModuleHandleW(name)
        print("    GetModuleHandleW(%-16s) = 0x%X" % (name, h or 0))

    print("")
    print("--- which Xingbian.Tsf.dll is mapped? ---")
    for name in ("Xingbian.Tsf.dll", "Xingbian.Tsf"):
        h = k32.GetModuleHandleW(name)
        if h:
            pp = path_of_module(h)
            print("    %s -> %s" % (name, pp))
            if pp and os.path.exists(pp):
                import hashlib
                with open(pp, "rb") as f:
                    print("    size=%d  sha256=%s" % (os.path.getsize(pp),
                          hashlib.sha256(f.read()).hexdigest()[:16]))
                break
    else:
        print("    no module named Xingbian.Tsf.dll is mapped under that exact name")
        print("    (the managed assembly may live inside a domain, not a PE image)")

    Release(p)
    return 0


if __name__ == "__main__":
    sys.exit(main())
