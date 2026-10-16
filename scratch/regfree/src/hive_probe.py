# -*- coding: utf-8 -*-
"""
hive_probe.py -- validate the SIX InprocServer32 registry values through the REAL
activation path, WITHOUT writing to the machine registry.

Two facts make this possible:

  * RegLoadAppKeyW() loads a registry HIVE FILE (a plain file in our scratch dir)
    and hands back an HKEY -- it works unprivileged, unlike writing HKLM/HKCU.
  * RegOverridePredefKey(HKEY_CLASSES_ROOT, hThatKey) redirects every HKCR lookup
    made by this process to our hive.

ole32!CoCreateInstance resolves the CLSID through HKCR\\CLSID\\{...}\\InprocServer32
and mscoree!DllGetClassObject reads Assembly/Class/RuntimeVersion from the very
same key -- so this exercises the identical code path as a real installation,
just with the key living in a file we own.

Usage:
    python hive_probe.py                     # real-world CodeBase (Program Files)
    python hive_probe.py --codebase app      # CodeBase pointed at our app dir
    python hive_probe.py --class Xingbian.Tsf.NoSuchClass
    python hive_probe.py --assembly "Xingbian.Tsf"
    python hive_probe.py --no-codebase
    python hive_probe.py --runtime v9.9.99999
"""

import argparse
import ctypes
import ctypes.wintypes as wintypes
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sxs_probe import (  # noqa: E402
    APP_DIR, GUID, guid, CLSID_TEXTSERVICE, IID_IUNKNOWN, u32, hr_text,
)

HERE = os.path.dirname(os.path.abspath(__file__))
HIVE_DIR = os.path.abspath(os.path.join(HERE, "..", "hive"))

advapi = ctypes.WinDLL("advapi32", use_last_error=True)
ole32 = ctypes.WinDLL("ole32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

HKEY_CLASSES_ROOT = 0x80000000
HKEY_CURRENT_USER = 0x80000001
HKEY_LOCAL_MACHINE = 0x80000002
HKEY_USERS = 0x80000003
HKEY_PERFORMANCE_DATA = 0x80000004
HKEY_CURRENT_CONFIG = 0x80000005

KEY_READ = 0x20019
KEY_ALL_ACCESS = 0xF003F
REG_SZ = 1
REG_OPTION_NON_VOLATILE = 0

advapi.RegLoadAppKeyW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(wintypes.HKEY),
                                  ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32]
advapi.RegLoadAppKeyW.restype = ctypes.c_long
advapi.RegCreateKeyExW.argtypes = [
    wintypes.HKEY, wintypes.LPCWSTR, ctypes.c_uint32, wintypes.LPWSTR,
    ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
    ctypes.POINTER(wintypes.HKEY), ctypes.POINTER(ctypes.c_uint32)]
advapi.RegCreateKeyExW.restype = ctypes.c_long
advapi.RegSetValueExW.argtypes = [wintypes.HKEY, wintypes.LPCWSTR, ctypes.c_uint32,
                                  ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32]
advapi.RegSetValueExW.restype = ctypes.c_long
advapi.RegQueryValueExW.argtypes = [
    wintypes.HKEY, wintypes.LPCWSTR, ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32),
    ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32)]
advapi.RegQueryValueExW.restype = ctypes.c_long
advapi.RegCloseKey.argtypes = [wintypes.HKEY]
advapi.RegCloseKey.restype = ctypes.c_long
advapi.RegOverridePredefKey.argtypes = [wintypes.HKEY, wintypes.HKEY]
advapi.RegOverridePredefKey.restype = ctypes.c_long

CLSID_KEY = "CLSID\\" + CLSID_TEXTSERVICE
IID_ITfTextInputProcessorEx = "6E4E2102-F9CD-433D-B496-303CE03A6507"
IID_ITfKeyEventSink = "AA80E7F5-2021-11D2-93E0-0060B067B86E"
IID_ITfTextInputProcessor = "AA80E7F7-2021-11D2-93E0-0060B067B86E"

CLSCTX_INPROC_SERVER = 1

# The six values install\register.ps1 undertakes to write (register.ps1:362-368).
MSCOREE = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "mscoree.dll")
REAL_INSTALL_DLL = r"C:\Program Files\XingbianIME\Xingbian.Tsf.dll"


def set_sz(hkey, name, value):
    data = ctypes.create_unicode_buffer(value)
    n = ctypes.sizeof(data)
    rc = advapi.RegSetValueExW(hkey, name, 0, REG_SZ, ctypes.byref(data), n)
    if rc != 0:
        raise OSError("RegSetValueExW(%s) rc=%d" % (name, rc))


def make_key(parent, sub):
    h = wintypes.HKEY()
    disp = ctypes.c_uint32(0)
    rc = advapi.RegCreateKeyExW(parent, sub, 0, None, REG_OPTION_NON_VOLATILE,
                                KEY_ALL_ACCESS, None, ctypes.byref(h), ctypes.byref(disp))
    if rc != 0:
        raise OSError("RegCreateKeyExW(%s) rc=%d" % (sub, rc))
    return h


def get_sz(hkey, name):
    typ = ctypes.c_uint32(0)
    n = ctypes.c_uint32(4096)
    buf = ctypes.create_unicode_buffer(2048)
    rc = advapi.RegQueryValueExW(hkey, name, None, ctypes.byref(typ),
                                 ctypes.byref(buf), ctypes.byref(n))
    if rc != 0:
        return None
    return buf.value


def build_tree(clsid_name, assembly, clazz, runtime, codebase):
    """Create HKCR-shaped CLSID tree inside the app-key hive; return the root HKEY."""
    if not os.path.isdir(HIVE_DIR):
        os.makedirs(HIVE_DIR)
    hiv = os.path.join(HIVE_DIR, "classes.hiv")
    if os.path.exists(hiv):
        try:
            os.remove(hiv)
        except OSError as ex:
            print("could not remove old hive: %s" % ex)
    hk = wintypes.HKEY()
    rc = advapi.RegLoadAppKeyW(hiv, ctypes.byref(hk), KEY_ALL_ACCESS, 0, 0)
    if rc != 0:
        raise OSError("RegLoadAppKeyW(%s) rc=%d" % (hiv, rc))
    print("RegLoadAppKeyW(%s) -> hKey=0x%X" % (hiv, hk.value or 0))

    k_clsid = make_key(hk, CLSID_KEY)
    set_sz(k_clsid, None, clsid_name)
    k_inproc = make_key(k_clsid, "InprocServer32")
    set_sz(k_inproc, None, MSCOREE)
    set_sz(k_inproc, "ThreadingModel", "Both")
    set_sz(k_inproc, "Assembly", assembly)
    set_sz(k_inproc, "Class", clazz)
    set_sz(k_inproc, "RuntimeVersion", runtime)
    if codebase is not None:
        set_sz(k_inproc, "CodeBase", codebase)
    k_progid = make_key(k_clsid, "ProgID")
    set_sz(k_progid, None, "Xingbian.Tsf.1")

    print("")
    print("--- hive contents (read back through the hive handle) ---")
    print("    HKCR\\%s\\(default) = %r" % (CLSID_KEY, get_sz(k_clsid, None)))
    for n in (None, "ThreadingModel", "Assembly", "Class", "RuntimeVersion", "CodeBase"):
        label = "(default)" if n is None else n
        print("    HKCR\\%s\\InprocServer32\\%s = %r" % (CLSID_KEY, label, get_sz(k_inproc, n)))
    print("")
    return hk


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--codebase", default="real",
                    help="'real' (Program Files), 'app' (our app dir), or 'none'")
    ap.add_argument("--class", dest="clazz", default="Xingbian.Tsf.TextService")
    ap.add_argument("--assembly", default="Xingbian.Tsf, Version=0.1.0.0, "
                                        "Culture=neutral, PublicKeyToken=null")
    ap.add_argument("--runtime", default="v4.0.30319")
    ap.add_argument("--no-codebase", action="store_true")
    args = ap.parse_args()

    if args.no_codebase or args.codebase == "none":
        codebase = None
    elif args.codebase == "app":
        codebase = "file:///" + os.path.join(APP_DIR, "Xingbian.Tsf.dll").replace("\\", "/")
    else:
        codebase = "file:///" + REAL_INSTALL_DLL.replace("\\", "/")

    print("=" * 78)
    print("hive_probe.py -- registry values validated WITHOUT touching the registry")
    print("=" * 78)
    print("CodeBase  = %r" % codebase)
    print("Class     = %r" % args.clazz)
    print("Assembly  = %r" % args.assembly)
    print("Runtime   = %r" % args.runtime)
    print("")

    hr = ole32.CoInitializeEx(None, 2)
    print("CoInitializeEx(APARTMENTTHREADED) hr=0x%08X" % u32(hr))

    hk = build_tree("Xingbian.Tsf.TextService", args.assembly, args.clazz,
                    args.runtime, codebase)

    rc = advapi.RegOverridePredefKey(HKEY_CLASSES_ROOT, hk)
    print("RegOverridePredefKey(HKCR, hiveKey) rc=%d %s" % (rc, "OK" if rc == 0 else "FAILED"))
    if rc != 0:
        return 2

    print("")
    print("-" * 78)
    print("ole32!CoCreateInstance(CLSID_TextService, IID_IUnknown)")
    print("-" * 78)
    g_clsid, g_iid = guid(CLSID_TEXTSERVICE), guid(IID_IUNKNOWN)
    out = ctypes.c_void_p(None)
    hr = ole32.CoCreateInstance(ctypes.byref(g_clsid), None, CLSCTX_INPROC_SERVER,
                                ctypes.byref(g_iid), ctypes.byref(out))
    print("RESULT CoCreateInstance              hr=0x%08X  %s" % (u32(hr), hr_text(hr)))
    if u32(hr) >= 0x80000000 or not out.value:
        print("")
        print(">>> could not create the object")
        advapi.RegOverridePredefKey(HKEY_CLASSES_ROOT, None)
        return 1

    print("OK: IUnknown* = 0x%X" % out.value)
    print("")
    print("--- which modules are now loaded ---")
    for name in ("mscoree.dll", "mscoreei.dll", "clr.dll", "mscorlib.ni.dll", "clrjit.dll"):
        h = kernel32.GetModuleHandleW(name)
        print("    GetModuleHandleW(%-16s) = 0x%X" % (name, h or 0))

    # ---- QI onto the TSF interfaces -----------------------------------------
    print("")
    print("-" * 78)
    print("QueryInterface for the TSF IIDs")
    print("-" * 78)
    vtbl = ctypes.cast(out.value, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    p_qi = vtbl[0]
    QI = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.POINTER(GUID),
                            ctypes.POINTER(ctypes.c_void_p))(p_qi)
    ptrs = {}
    for label, iid in (("ITfTextInputProcessorEx", IID_ITfTextInputProcessorEx),
                       ("ITfKeyEventSink", IID_ITfKeyEventSink),
                       ("ITfTextInputProcessor", IID_ITfTextInputProcessor),
                       ("IID_IUnknown", IID_IUNKNOWN)):
        g = guid(iid)
        p = ctypes.c_void_p(None)
        qhr = QI(out.value, ctypes.byref(g), ctypes.byref(p))
        print("RESULT QI %-24s hr=0x%08X  %-28s ptr=0x%X"
              % (label, u32(qhr), hr_text(qhr), p.value or 0))
        if u32(qhr) < 0x80000000 and p.value:
            ptrs[label] = p.value

    # ---- really call a method ----------------------------------------------
    print("")
    print("-" * 78)
    print("real method calls through the vtables")
    print("-" * 78)
    if "ITfTextInputProcessorEx" in ptrs:
        p = ptrs["ITfTextInputProcessorEx"]
        slot = ctypes.cast(p, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents[5]
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.c_void_p,
                                ctypes.c_uint32, ctypes.c_uint32)(slot)
        chr_ = fn(p, None, 0, 0)
        print("RESULT ITfTextInputProcessorEx::ActivateEx(NULL,0,0) hr=0x%08X  %s"
              % (u32(chr_), hr_text(chr_)))
        print("       TextService.cs:181 -> `if (ptim == null) return E_INVALIDARG;`")
        print("       0x80070057 here proves the MANAGED BODY ran in this process.")
    if "ITfKeyEventSink" in ptrs:
        p = ptrs["ITfKeyEventSink"]
        slot = ctypes.cast(p, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents[3]
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p, ctypes.c_int)(slot)
        chr_ = fn(p, 0)
        print("RESULT ITfKeyEventSink::OnSetFocus(FALSE)          hr=0x%08X  %s"
              % (u32(chr_), hr_text(chr_)))
    if "ITfTextInputProcessor" in ptrs:
        p = ptrs["ITfTextInputProcessor"]
        slot = ctypes.cast(p, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents[1]
        fn = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_void_p)(slot)
        chr_ = fn(p)
        print("RESULT ITfTextInputProcessor::Deactivate()         hr=0x%08X  %s"
              % (u32(chr_), hr_text(chr_)))

    advapi.RegOverridePredefKey(HKEY_CLASSES_ROOT, None)
    print("")
    print("(HKCR override released)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
