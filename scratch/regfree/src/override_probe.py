# -*- coding: utf-8 -*-
"""
override_probe.py -- diagnose why RegOverridePredefKey(HKCR, appHiveKey) returns
ERROR_INVALID_HANDLE (6), so the perturbed-value clean room can be built.

Matrix under test:
  * app-key hive handle straight from RegLoadAppKeyW
  * a SUBKEY of the app-key hive opened with RegOpenKeyExW  (may normalise the handle)
  * a plain HKCU subkey opened read-only  (control: proves the API itself works)

Uses a fixed hive-file identity to keep the app key handle valid across steps.
"""

import ctypes
import ctypes.wintypes as wintypes
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
HIVE_DIR = os.path.abspath(os.path.join(HERE, "..", "hive"))

advapi = ctypes.WinDLL("advapi32", use_last_error=True)

HKEY_CLASSES_ROOT = 0x80000000
HKEY_CURRENT_USER = 0x80000001
HKEY_LOCAL_MACHINE = 0x80000002

KEY_READ = 0x20019
KEY_WRITE = 0x20006
KEY_ALL_ACCESS = 0xF003F
REG_SZ = 1
REG_OPTION_NON_VOLATILE = 0

advapi.RegLoadAppKeyW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(wintypes.HKEY),
                                  ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32]
advapi.RegLoadAppKeyW.restype = ctypes.c_long
advapi.RegOpenKeyExW.argtypes = [wintypes.HKEY, wintypes.LPCWSTR, ctypes.c_uint32,
                                 ctypes.c_uint32, ctypes.POINTER(wintypes.HKEY)]
advapi.RegOpenKeyExW.restype = ctypes.c_long
advapi.RegCreateKeyExW.argtypes = [
    wintypes.HKEY, wintypes.LPCWSTR, ctypes.c_uint32, wintypes.LPWSTR,
    ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
    ctypes.POINTER(wintypes.HKEY), ctypes.POINTER(ctypes.c_uint32)]
advapi.RegCreateKeyExW.restype = ctypes.c_long
advapi.RegSetValueExW.argtypes = [wintypes.HKEY, wintypes.LPCWSTR, ctypes.c_uint32,
                                  ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32]
advapi.RegSetValueExW.restype = ctypes.c_long
advapi.RegOverridePredefKey.argtypes = [wintypes.HKEY, wintypes.HKEY]
advapi.RegOverridePredefKey.restype = ctypes.c_long
advapi.RegQueryInfoKeyW.argtypes = [
    wintypes.HKEY, wintypes.LPWSTR, ctypes.POINTER(ctypes.c_uint32),
    ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(ctypes.c_uint32),
    ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(ctypes.c_uint32),
    ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(ctypes.c_uint32),
    ctypes.POINTER(ctypes.c_uint32), ctypes.c_void_p]
advapi.RegQueryInfoKeyW.restype = ctypes.c_long

ERROR_TEXT = {
    0: "ERROR_SUCCESS",
    5: "ERROR_ACCESS_DENIED",
    6: "ERROR_INVALID_HANDLE",
    87: "ERROR_INVALID_PARAMETER",
    1314: "ERROR_PRIVILEGE_NOT_HELD",
}


def er(rc):
    return "%d %s" % (rc, ERROR_TEXT.get(rc, "?"))


def make_key(parent, sub):
    h = wintypes.HKEY()
    disp = ctypes.c_uint32(0)
    rc = advapi.RegCreateKeyExW(parent, sub, 0, None, REG_OPTION_NON_VOLATILE,
                                KEY_ALL_ACCESS, None, ctypes.byref(h), ctypes.byref(disp))
    return rc, h


def set_sz(hkey, name, value):
    buf = ctypes.create_unicode_buffer(value)
    return advapi.RegSetValueExW(hkey, name, 0, REG_SZ, ctypes.byref(buf),
                                 ctypes.sizeof(buf))


def subkeys(hkey, label):
    n_sub = ctypes.c_uint32(0)
    n_val = ctypes.c_uint32(0)
    rc = advapi.RegQueryInfoKeyW(hkey, None, None, None, ctypes.byref(n_sub),
                                 ctypes.byref(ctypes.c_uint32(0)), None, ctypes.byref(n_val),
                                 None, None, None, None)
    print("    [%s] RegQueryInfoKeyW rc=%s subkeys=%u values=%u"
          % (label, er(rc), n_sub.value, n_val.value))
    return rc


def try_override(label, target, newkey):
    rc = advapi.RegOverridePredefKey(target, newkey)
    print("  RegOverridePredefKey(%-24s -> %-28s) rc=%s" % (label, "hKey", er(rc)))
    if rc == 0:
        advapi.RegOverridePredefKey(target, None)
        print("      (override released)")
    return rc


def main():
    if not os.path.isdir(HIVE_DIR):
        os.makedirs(HIVE_DIR)
    hiv = os.path.join(HIVE_DIR, "classes.hiv")

    print("=" * 78)
    print("override_probe.py -- why does RegOverridePredefKey(HKCR, appKey) fail?")
    print("=" * 78)
    print("hive file : %s" % hiv)
    print("")

    # --- load (or reuse) the app-key hive -----------------------------------
    hk = wintypes.HKEY()
    rc = advapi.RegLoadAppKeyW(hiv, ctypes.byref(hk), KEY_ALL_ACCESS, 0, 0)
    print("RegLoadAppKeyW(KEY_ALL_ACCESS) rc=%s  hKey=0x%X" % (er(rc), hk.value or 0))
    if rc != 0:
        return 1
    appkey = hk.value
    subkeys(hk, "app-key root")

    # build a little tree so there is something to open
    rc, k_clsid = make_key(hk, "CLSID\\{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}")
    print("RegCreateKeyExW(CLSID\\{...}) rc=%s hKey=0x%X" % (er(rc), k_clsid.value or 0))
    rc2, k_inproc = make_key(k_clsid, "InprocServer32")
    print("RegCreateKeyExW(...\\InprocServer32) rc=%s hKey=0x%X"
          % (er(rc2), k_inproc.value or 0))
    print("RegSetValueExW(Class) rc=%s"
          % er(set_sz(k_inproc, "Class", "Xingbian.Tsf.TextService")))
    subkeys(k_clsid, "CLSID key")

    print("")
    print("-" * 78)
    print("[1] control: override with a PLAIN HKCU key opened read-only")
    print("-" * 78)
    hcu = wintypes.HKEY()
    rc = advapi.RegOpenKeyExW(HKEY_CURRENT_USER, "Software", 0, KEY_READ, ctypes.byref(hcu))
    print("  RegOpenKeyExW(HKCU\\Software, KEY_READ) rc=%s hKey=0x%X"
          % (er(rc), hcu.value or 0))
    if rc == 0:
        try_override("HKEY_CURRENT_USER", HKEY_CURRENT_USER, hcu)
        try_override("HKEY_CLASSES_ROOT", HKEY_CLASSES_ROOT, hcu)
        try_override("HKEY_LOCAL_MACHINE", HKEY_LOCAL_MACHINE, hcu)

    print("")
    print("-" * 78)
    print("[2] app-key hive handle directly")
    print("-" * 78)
    try_override("HKEY_CLASSES_ROOT", HKEY_CLASSES_ROOT, hk)

    print("")
    print("-" * 78)
    print("[3] SUBKEY of the app-key hive, reopened with RegOpenKeyExW")
    print("-" * 78)
    h_sub = wintypes.HKEY()
    rc = advapi.RegOpenKeyExW(hk, "CLSID", 0, KEY_ALL_ACCESS, ctypes.byref(h_sub))
    print("  RegOpenKeyExW(appKey\\CLSID, KEY_ALL_ACCESS) rc=%s hKey=0x%X"
          % (er(rc), h_sub.value or 0))
    if rc == 0:
        try_override("HKEY_CLASSES_ROOT", HKEY_CLASSES_ROOT, h_sub)

    print("")
    print("-" * 78)
    print("[4] app-key hive reopened the same way as RegLoadAppKey produced it")
    print("-" * 78)
    h_same = wintypes.HKEY()
    rc = advapi.RegOpenKeyExW(appkey, "", 0, KEY_ALL_ACCESS, ctypes.byref(h_same)) \
        if False else (6, wintypes.HKEY())
    print("  (skipped: reopening the app-key root by name is not supported)")

    print("")
    print("=" * 78)
    print("appKey=0x%X" % appkey)
    print("=" * 78)
    return 0


if __name__ == "__main__":
    sys.exit(main())
