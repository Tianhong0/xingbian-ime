# -*- coding: utf-8 -*-
"""
slot_probe.py -- call ONE vtable slot of ONE QI'd interface per process, with an
exact argument arity, so a faulting slot cannot hide the others' results.

The vtable arithmetic in vtable_probe.py suggested the CCW's
ITfTextInputProcessorEx vtable is only 4 entries (3 + ONE method) because the
thunk for that interface sits 0xA bytes past the end of a 4-entry array, and the
same 0xA gap reproduces for ITfTextInputProcessor (5 entries / 2 methods) and
ITfKeyEventSink (9 entries / 6 methods). This probe tests that claim by calling
the suspected non-method cells directly.

    python slot_probe.py --ptr tip --slot 5
    python slot_probe.py --ptr ex  --slot 4
    python slot_probe.py --ptr ex  --slot 3
"""

import argparse
import ctypes
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sxs_probe import (  # noqa: E402
    GUID, guid, CLSID_TEXTSERVICE, IID_IUNKNOWN, u32, hr_text,
)

ole32 = ctypes.WinDLL("ole32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)

CLSCTX_INPROC_SERVER = 1
IID_ITfTextInputProcessorEx = "6E4E2102-F9CD-433D-B496-303CE03A6507"
IID_ITfKeyEventSink = "AA80E7F5-2021-11D2-93E0-0060B067B86E"
IID_ITfTextInputProcessor = "AA80E7F7-2021-11D2-93E0-0060B067B86E"

GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004
GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002

kernel32.GetModuleHandleExW.argtypes = [ctypes.c_uint32, ctypes.c_void_p,
                                        ctypes.POINTER(ctypes.c_void_p)]
kernel32.GetModuleHandleExW.restype = ctypes.c_int
kernel32.GetCurrentProcess.restype = ctypes.c_void_p
psapi.GetModuleBaseNameW.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                     ctypes.c_wchar_p, ctypes.c_uint32]
psapi.GetModuleBaseNameW.restype = ctypes.c_uint32


def module_of(addr):
    if not addr:
        return "-"
    hm = ctypes.c_void_p(None)
    ok = kernel32.GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        ctypes.c_void_p(addr), ctypes.byref(hm))
    if not ok or not hm.value:
        return "(no module)"
    buf = ctypes.create_unicode_buffer(260)
    n = psapi.GetModuleBaseNameW(kernel32.GetCurrentProcess(), hm, buf, 260)
    if n == 0:
        return "(module@0x%X)" % hm.value
    return buf.value


def vtbl(p):
    return ctypes.cast(p, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents


def slot(p, i):
    return vtbl(p)[i]


def QI(p, iid_str):
    fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.POINTER(GUID),
                            ctypes.POINTER(ctypes.c_void_p))(slot(p, 0))
    g = guid(iid_str)
    out = ctypes.c_void_p(None)
    hr = fn(p, ctypes.byref(g), ctypes.byref(out))
    return u32(hr), out.value


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ptr", required=True, choices=["ex", "tip", "kes", "unk"])
    ap.add_argument("--slot", type=int, required=True)
    ap.add_argument("--argc", type=int, default=1,
                    help="1 = (this) ; 3 = (this, NULL, 0) ; 4 = (this, NULL, 0, 0)")
    args = ap.parse_args()

    print("=" * 78)
    print("slot_probe.py ptr=%s slot=%d argc=%d pid=%d"
          % (args.ptr, args.slot, args.argc, os.getpid()))
    print("=" * 78)
    hr = ole32.CoInitializeEx(None, 2)
    print("CoInitializeEx hr=0x%08X" % u32(hr))

    g_clsid, g_iid = guid(CLSID_TEXTSERVICE), guid(IID_IUNKNOWN)
    p = ctypes.c_void_p(None)
    hr = ole32.CoCreateInstance(ctypes.byref(g_clsid), None, CLSCTX_INPROC_SERVER,
                                ctypes.byref(g_iid), ctypes.byref(p))
    print("CoCreateInstance hr=0x%08X %s" % (u32(hr), hr_text(hr)))
    if u32(hr) >= 0x80000000 or not p.value:
        return 1
    base = p.value

    ptrs = {"unk": base}
    for key, iid in (("ex", IID_ITfTextInputProcessorEx),
                     ("kes", IID_ITfKeyEventSink),
                     ("tip", IID_ITfTextInputProcessor)):
        qhr, qp = QI(base, iid)
        print("QI %-4s hr=0x%08X ptr=0x%X" % (key, u32(qhr), qp or 0))
        if u32(qhr) < 0x80000000 and qp:
            ptrs[key] = qp

    q = ptrs.get(args.ptr)
    if not q:
        print("no pointer for %s" % args.ptr)
        return 1

    vt = ctypes.cast(q, ctypes.POINTER(ctypes.c_void_p))[0]
    addr = slot(q, args.slot)
    print("")
    print("this=0x%X  vtbl=0x%X" % (q, vt or 0))
    print("vtable[%d] = 0x%X   owner=%s" % (args.slot, addr or 0, module_of(addr)))
    print(">>> calling it now (a fault here is a RESULT, not a harness error)")

    if args.argc == 1:
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)(addr)
        r = fn(q)
    elif args.argc == 3:
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.c_void_p,
                                ctypes.c_uint32)(addr)
        r = fn(q, None, 0)
    elif args.argc == 4:
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.c_void_p,
                                ctypes.c_uint32, ctypes.c_uint32)(addr)
        r = fn(q, None, 0, 0)
    else:
        print("unsupported argc")
        return 2
    print("RESULT returned hr=0x%08X  %s" % (u32(r), hr_text(r)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
