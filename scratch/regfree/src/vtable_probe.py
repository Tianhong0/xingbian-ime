# -*- coding: utf-8 -*-
"""
vtable_probe.py -- characterise the CCW vtables that the CLR hands back for
Xingbian.Tsf.TextService, to tell "my slot arithmetic is wrong" apart from
"the CCW really does not expose ITfTextInputProcessorEx::ActivateEx at slot 5".

For every QI'd interface pointer we print:
  * the interface pointer, its stored vtable pointer
  * raw qwords from vtbl-32 .. vtbl+15*8 (so we can see where a vtable starts/ends)
  * for each slot: the address, the module that owns it, and its protection
A genuinely short vtable shows as executable slots followed by data/null;
an offset error shows as the SAME method appearing at shifted indices.
"""

import ctypes
import ctypes.wintypes as wintypes
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

MEM_COMMIT = 0x1000
PAGE_GUARD = 0x100
PAGE_NOACCESS = 0x01
EXEC_MASK = 0x10 | 0x20 | 0x40 | 0x80

GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004
GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002


class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p),
                ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", ctypes.c_uint32),
                ("PartitionId", ctypes.c_uint16),
                ("__alignment1", ctypes.c_uint16),
                ("RegionSize", ctypes.c_size_t),
                ("State", ctypes.c_uint32),
                ("Protect", ctypes.c_uint32),
                ("Type", ctypes.c_uint32),
                ("__alignment2", ctypes.c_uint32)]


kernel32.VirtualQuery.argtypes = [ctypes.c_void_p,
                                  ctypes.POINTER(MEMORY_BASIC_INFORMATION),
                                  ctypes.c_size_t]
kernel32.VirtualQuery.restype = ctypes.c_size_t
kernel32.GetModuleHandleExW.argtypes = [ctypes.c_uint32, ctypes.c_void_p,
                                        ctypes.POINTER(ctypes.c_void_p)]
kernel32.GetModuleHandleExW.restype = ctypes.c_int
psapi.GetModuleBaseNameW.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                     wintypes.LPWSTR, ctypes.c_uint32]
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
    psapi.GetModuleBaseNameW(None, hm, buf, 260)
    return buf.value or "(?)"


def protect_of(addr):
    if not addr:
        return "null"
    mbi = MEMORY_BASIC_INFORMATION()
    n = kernel32.VirtualQuery(ctypes.c_void_p(addr), ctypes.byref(mbi),
                              ctypes.sizeof(mbi))
    if n == 0:
        return "UNMAPPED"
    if mbi.State != MEM_COMMIT:
        return "state=0x%X" % mbi.State
    p = mbi.Protect
    kind = "EXEC" if (p & EXEC_MASK) else ("NOACCESS" if (p & PAGE_NOACCESS) else "DATA")
    return "prot=0x%X %s" % (p, kind)


def vtbl_ptr(p):
    return ctypes.cast(p, ctypes.POINTER(ctypes.c_void_p))[0]


def slot(p, i):
    return ctypes.cast(p, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents[i]


def raw_at(addr):
    return ctypes.cast(ctypes.c_void_p(addr), ctypes.POINTER(ctypes.c_void_p))[0]


def QI(p, iid_str):
    fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.POINTER(GUID),
                            ctypes.POINTER(ctypes.c_void_p))(slot(p, 0))
    g = guid(iid_str)
    out = ctypes.c_void_p(None)
    hr = fn(p, ctypes.byref(g), ctypes.byref(out))
    return u32(hr), out.value


def main():
    print("=" * 78)
    print("vtable_probe.py  pid=%d" % os.getpid())
    print("=" * 78)
    ole32.CoInitializeEx(None, 2)

    for m in ("mscoree.dll", "mscoreei.dll", "clr.dll", "mscorlib.ni.dll",
              "clrjit.dll", "Xingbian.Tsf.dll", "Xingbian.Core.dll"):
        ads = kernel32.GetModuleHandleW(m)
        print("  GetModuleHandleW(%-18s) = 0x%X" % (m, ads or 0))

    g_clsid, g_iid = guid(CLSID_TEXTSERVICE), guid(IID_IUNKNOWN)
    p = ctypes.c_void_p(None)
    hr = ole32.CoCreateInstance(ctypes.byref(g_clsid), None, CLSCTX_INPROC_SERVER,
                                ctypes.byref(g_iid), ctypes.byref(p))
    print("")
    print("CoCreateInstance hr=0x%08X %s  IUnknown*=0x%X"
          % (u32(hr), hr_text(hr), p.value or 0))
    if u32(hr) >= 0x80000000 or not p.value:
        return 1

    base = p.value
    seen = {}
    order = [("IUnknown (as returned)", base)]
    for label, iid in (("ITfTextInputProcessorEx", IID_ITfTextInputProcessorEx),
                       ("ITfKeyEventSink", IID_ITfKeyEventSink),
                       ("ITfTextInputProcessor", IID_ITfTextInputProcessor)):
        qhr, qp = QI(base, iid)
        print("QI %-26s hr=0x%08X ptr=0x%X  (delta from IUnknown = %+d)"
              % (label, u32(qhr), qp or 0, (qp or 0) - base))
        if u32(qhr) < 0x80000000 and qp:
            order.append((label, qp))
            seen[label] = qp

    print("")
    print("--- vtable pointers (equal means CLR shares one array) ---")
    for label, q in order:
        print("  %-26s this=0x%016X  vtbl=0x%016X  module_of(vtbl)=%s"
              % (label, q, vtbl_ptr(q) or 0, module_of(vtbl_ptr(q))))

    for label, q in order:
        v = vtbl_ptr(q)
        print("")
        print("=" * 78)
        print("%s   this=0x%X   vtbl=0x%X" % (label, q, v or 0))
        print("=" * 78)
        print("  --- raw qwords vtbl-0x20 .. vtbl+0xA0 ---")
        for k in range(-4, 21):
            try:
                a = raw_at((v or 0) + k * 8)
            except Exception:
                a = None
            mark = ""
            if k == 0:
                mark = "  <-- slot[0] QI"
            elif k == 1:
                mark = "  <-- slot[1] AddRef"
            elif k == 2:
                mark = "  <-- slot[2] Release"
            elif k >= 3:
                mark = "  <-- slot[%d]" % k
            print("    [%+3d] 0x%016X  %-22s %s%s"
                  % (k, a or 0, module_of(a), protect_of(a), mark))
    return 0


if __name__ == "__main__":
    sys.exit(main())
