# -*- coding: utf-8 -*-
"""
call_probe.py -- call ONE method per process, so an access violation in one call
cannot hide the results of the others.

Every mode does:  CoInitializeEx -> CoCreateInstance -> QI -> one vtable call.

Modes:
    dump              print vtable slots 0..7 for each TSF interface pointer
    onsetfocus        ITfKeyEventSink::OnSetFocus(FALSE)          (vtable slot 3)
    activate_null     ITfTextInputProcessor::Activate(NULL,0)     (vtable slot 3)
    activateex_null   ITfTextInputProcessorEx::ActivateEx(NULL,0,0) (vtable slot 5)
    deactivate        ITfTextInputProcessor::Deactivate()          (vtable slot 4)
    safe_calls        onsetfocus + deactivate, nothing else
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

CLSCTX_INPROC_SERVER = 1
IID_ITfTextInputProcessorEx = "6E4E2102-F9CD-433D-B496-303CE03A6507"
IID_ITfKeyEventSink = "AA80E7F5-2021-11D2-93E0-0060B067B86E"
IID_ITfTextInputProcessor = "AA80E7F7-2021-11D2-93E0-0060B067B86E"


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


def pr(label, hr):
    h = u32(hr)
    print("RESULT %-52s hr=0x%08X  %s  %s"
          % (label, h, "OK" if h < 0x80000000 else "FAIL", hr_text(hr)))
    sys.stdout.flush()
    return h


def get_object():
    g_clsid, g_iid = guid(CLSID_TEXTSERVICE), guid(IID_IUNKNOWN)
    out = ctypes.c_void_p(None)
    hr = ole32.CoCreateInstance(ctypes.byref(g_clsid), None, CLSCTX_INPROC_SERVER,
                                ctypes.byref(g_iid), ctypes.byref(out))
    print("RESULT CoCreateInstance(CLSID, IID_IUnknown)          hr=0x%08X  %s  %s"
          % (u32(hr), "OK" if u32(hr) < 0x80000000 else "FAIL", hr_text(hr)))
    if u32(hr) >= 0x80000000 or not out.value:
        return None
    print("       IUnknown* = 0x%X" % out.value)
    return out.value


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--call", required=True)
    args = ap.parse_args()

    print("=" * 78)
    print("call_probe.py  call=%s  pid=%d" % (args.call, os.getpid()))
    print("=" * 78)

    hr = ole32.CoInitializeEx(None, 2)
    pr("CoInitializeEx(APARTMENTTHREADED)", hr)

    p = get_object()
    if not p:
        print(">>> no object; nothing to call")
        return 1

    hrE, pEx = QI(p, IID_ITfTextInputProcessorEx)
    hrK, pKey = QI(p, IID_ITfKeyEventSink)
    hrT, pTip = QI(p, IID_ITfTextInputProcessor)
    pr("QI ITfTextInputProcessorEx", hrE)
    pr("QI ITfKeyEventSink", hrK)
    pr("QI ITfTextInputProcessor", hrT)

    if args.call == "dump":
        for name, q in (("ITfTextInputProcessorEx", pEx),
                        ("ITfKeyEventSink", pKey),
                        ("ITfTextInputProcessor", pTip)):
            if not q:
                continue
            print("--- vtable of %s (this=0x%X) ---" % (name, q))
            v = vtbl(q)
            for i in range(8):
                try:
                    print("    slot[%d] = 0x%X" % (i, v[i]))
                except Exception as ex:
                    print("    slot[%d] unavailable: %s" % (i, ex))
        return 0

    if args.call == "onsetfocus":
        if not pKey:
            return 1
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p,
                                ctypes.c_int)(slot(pKey, 3))
        print(">>> calling ITfKeyEventSink vtable[3] with (this, FALSE)")
        pr("ITfKeyEventSink::OnSetFocus(FALSE)", fn(pKey, 0))
        return 0

    if args.call == "activate_null":
        if not pTip:
            return 1
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.c_void_p,
                                ctypes.c_uint32)(slot(pTip, 3))
        print(">>> calling ITfTextInputProcessor vtable[3] with (this, NULL, 0)")
        pr("ITfTextInputProcessor::Activate(NULL,0)", fn(pTip, None, 0))
        return 0

    if args.call == "activateex_null":
        if not pEx:
            return 1
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.c_void_p,
                                ctypes.c_uint32, ctypes.c_uint32)(slot(pEx, 5))
        print(">>> calling ITfTextInputProcessorEx vtable[5] with (this, NULL, 0, 0)")
        pr("ITfTextInputProcessorEx::ActivateEx(NULL,0,0)", fn(pEx, None, 0, 0))
        return 0

    if args.call == "deactivate":
        if not pTip:
            return 1
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)(slot(pTip, 4))
        print(">>> calling ITfTextInputProcessor vtable[4] with (this)")
        pr("ITfTextInputProcessor::Deactivate()", fn(pTip))
        return 0

    if args.call == "safe_calls":
        if pKey:
            fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p,
                                    ctypes.c_int)(slot(pKey, 3))
            pr("ITfKeyEventSink::OnSetFocus(FALSE)", fn(pKey, 0))
        if pTip:
            fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)(slot(pTip, 4))
            pr("ITfTextInputProcessor::Deactivate()", fn(pTip))
        return 0

    print("unknown --call %s" % args.call)
    return 2


if __name__ == "__main__":
    sys.exit(main())
