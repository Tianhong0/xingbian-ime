# -*- coding: utf-8 -*-
"""codebase_proof.py -- which FILE on disk did the CLR actually load?

The whole question "is CodeBase honoured?" reduces to a filesystem fact: the
mapped image's path.  This probe enumerates every module in the process, finds
the one whose file name is Xingbian.Tsf.dll / Xingbian.Core.dll, and prints its
FULL path plus SHA-256.  Comparing that path against the registry CodeBase value
tells us, with no ambiguity, which of the two competing locations won.

Run it twice:
  * with a python.exe whose directory CONTAINS Xingbian.Tsf.dll  -> app base wins
  * with a python.exe whose directory LACKS  Xingbian.Tsf.dll    -> CodeBase wins
"""

import ctypes
import hashlib
import os
import sys
import winreg
from ctypes import POINTER, byref, c_int32, c_uint32, c_void_p, c_wchar_p

CLSID_TEXTSERVICE = "{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}"
IID_IUNKNOWN = "{00000000-0000-0000-C000-000000000046}"
IID_IEX = "{6E4E2102-F9CD-433D-B496-303CE03A6507}"

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
ole32 = ctypes.WinDLL("ole32", use_last_error=True)


class GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_uint32), ("Data2", ctypes.c_uint16),
                ("Data3", ctypes.c_uint16), ("Data4", ctypes.c_ubyte * 8)]


def guid(s):
    import uuid
    u = uuid.UUID(s.strip("{}"))
    g = GUID()
    g.Data1, g.Data2, g.Data3 = u.time_low, u.time_mid, u.time_hi_version
    for i, b in enumerate(u.bytes[8:]):
        g.Data4[i] = b
    return g


k32.GetModuleFileNameExW.argtypes = [c_void_p, c_void_p, c_wchar_p, c_uint32]
k32.GetModuleFileNameExW.restype = c_uint32
psapi.EnumProcessModules.argtypes = [c_void_p, POINTER(c_void_p), c_uint32,
                                     POINTER(c_uint32)]
psapi.EnumProcessModules.restype = c_int32
k32.GetCurrentProcess.restype = c_void_p
ole32.CoCreateInstance.argtypes = [POINTER(GUID), c_void_p, c_uint32,
                                   POINTER(GUID), POINTER(c_void_p)]
ole32.CoCreateInstance.restype = c_int32

TARGETS = ("xingbian.tsf.dll", "xingbian.core.dll")


def mapped_files():
    """Every loaded module whose file name is one of our assemblies -> full path."""
    h = k32.GetCurrentProcess()
    need = c_uint32(0)
    psapi.EnumProcessModules(h, None, 0, byref(need))
    n = need.value // ctypes.sizeof(c_void_p) + 64
    arr = (c_void_p * n)()
    got = c_uint32(0)
    if not psapi.EnumProcessModules(h, arr, ctypes.sizeof(arr), byref(got)):
        return []
    out = []
    for i in range(got.value // ctypes.sizeof(c_void_p)):
        if not arr[i]:
            continue
        buf = ctypes.create_unicode_buffer(1024)
        if k32.GetModuleFileNameExW(h, arr[i], buf, 1024):
            if os.path.basename(buf.value).lower() in TARGETS:
                out.append((arr[i], buf.value))
    return out


def sha16(path):
    try:
        with open(path, "rb") as f:
            return hashlib.sha256(f.read()).hexdigest()[:16].upper()
    except OSError:
        return "?"


def registry_codebase():
    p = (r"SOFTWARE\Classes\CLSID\%s\InprocServer32" % CLSID_TEXTSERVICE)
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, p, 0, winreg.KEY_READ) as k:
            return winreg.QueryValueEx(k, "CodeBase")[0]
    except OSError as ex:
        return "<unreadable: %s>" % ex


def main():
    base = os.path.dirname(sys.executable)
    print("=" * 78)
    print("codebase_proof.py -- which file did the CLR load, and why?")
    print("=" * 78)
    print("host exe    : %s" % sys.executable)
    print("app base dir: %s" % base)
    local = os.path.join(base, "Xingbian.Tsf.dll")
    print("app base Xingbian.Tsf.dll : %s"
          % ("EXISTS  %d B  sha16=%s" % (os.path.getsize(local), sha16(local))
             if os.path.isfile(local) else "ABSENT"))
    cb = registry_codebase()
    print("registry CodeBase         : %s" % cb)
    cb_path = cb.replace("file:///", "").replace("/", "\\") if cb.startswith("file:///") else cb
    print("  -> as a Windows path    : %s  (exists=%s)"
          % (cb_path, os.path.isfile(cb_path)))

    ole32.CoInitializeEx(None, 2)
    hr = ole32.CoCreateInstance(byref(guid(CLSID_TEXTSERVICE)), None, 1,
                                byref(guid(IID_IUNKNOWN)), byref(c_void_p()))
    print("\nCoCreateInstance hr=0x%08X" % (hr & 0xFFFFFFFF))

    print("\n--- our assemblies mapped into this process ---")
    found = mapped_files()
    if not found:
        print("    (none)")
    for _, path in found:
        print("    %s" % path)
        print("        sha16=%s   exists_now=%s" % (sha16(path), os.path.isfile(path)))
        if os.path.normcase(os.path.abspath(path)) == os.path.normcase(os.path.abspath(cb_path)):
            print("        *** loaded from the registry CodeBase location ***")
        if os.path.normcase(os.path.dirname(path)) == os.path.normcase(os.path.abspath(base)):
            print("        *** loaded from the APPLICATION BASE directory ***")

    print("\n--- verdict ---")
    ts = [p for _, p in found if os.path.basename(p).lower() == "xingbian.tsf.dll"]
    if not ts:
        print("    Xingbian.Tsf.dll was never loaded (activation failed).")
    else:
        win = ts[0]
        if os.path.normcase(os.path.dirname(win)) == os.path.normcase(os.path.abspath(base)):
            print("    APP BASE won: the CLR preferred the copy sitting next to the exe,")
            print("    and the registry CodeBase was NOT used to choose the file.")
        else:
            print("    CodeBase won (app base had no copy): %s" % win)
    return 0


if __name__ == "__main__":
    sys.exit(main())
