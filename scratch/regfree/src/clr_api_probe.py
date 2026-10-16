# -*- coding: utf-8 -*-
"""clr_api_probe.py -- ask the CLR *directly* what a malformed Class/Assembly
value does, without writing a single registry key.

Why this works
--------------
mscoree.dll exports the CLR's own public entry point for "build me an instance
of this managed type":

    HRESULT WINAPI ClrCreateManagedInstance(LPCWSTR pTypeName,
                                            REFIID   riid,
                                            void   **ppObject);

The type name it wants is EXACTLY the string mscoree assembles out of the two
InprocServer32 string values when it services DllGetClassObject:

    InprocServer32\\Class  +  ", "  +  InprocServer32\\Assembly

So by feeding this API deliberately malformed names we observe the precise
HRESULT the shipped product would report for each bad registry value -- on a
machine where we are forbidden to write the registry at all.

Caveat (stated in RESULTS.md): this bypasses the registry lookup in front of it,
so it proves what the *CLR activation layer* does with each string. It does not
re-prove that the lookup itself is correct -- for that see the end-to-end
registry_route run.
"""

import ctypes
import os
import sys
from ctypes import POINTER, byref, c_int32, c_void_p, c_wchar_p

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

IID_IUNKNOWN = "{00000000-0000-0000-C000-000000000046}"


class GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_uint32),
                ("Data2", ctypes.c_uint16),
                ("Data3", ctypes.c_uint16),
                ("Data4", ctypes.c_ubyte * 8)]


def guid(s):
    import uuid
    u = uuid.UUID(s.strip("{}"))
    g = GUID()
    g.Data1, g.Data2, g.Data3 = u.time_low, u.time_mid, u.time_hi_version
    for i, b in enumerate(u.bytes[8:]):
        g.Data4[i] = b
    return g


def hresult_name(hr):
    names = {
        0x00000000: "S_OK",
        0x80004002: "E_NOINTERFACE",
        0x80040154: "REGDB_E_CLASSNOTREG",
        0x80070002: "HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)",
        0x80070057: "E_INVALIDARG",
        0x80131040: "COR_E_TYPELOAD / FileLoadException (assembly identity mismatch)",
        0x80131045: "CLR_E_BIND_TYPE_NOT_FOUND (class not found in assembly)",
        0x8013101B: "CLR_E_SHIM_RUNTIMELOAD",
        0x80131700: "CLR_E_SHIM_RUNTIMELOAD",
        0x80040111: "CLASS_E_CLASSNOTAVAILABLE",
    }
    return names.get(hr & 0xFFFFFFFF, "?")


def query_registry_values():
    """Read (never write) the two values whose concatenation we are modelling."""
    import winreg
    path = (r"SOFTWARE\Classes\CLSID"
            r"\{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}\InprocServer32")
    out = {}
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path, 0, winreg.KEY_READ) as k:
            for name in ("Class", "Assembly", "RuntimeVersion", "CodeBase"):
                try:
                    out[name] = winreg.QueryValueEx(k, name)[0]
                except OSError:
                    out[name] = None
    except OSError as ex:
        out["_error"] = str(ex)
    return out


CASES = [
    # (label, type name handed to ClrCreateManagedInstance)
    ("H0 exact concatenation of the two installed values",
     "Xingbian.Tsf.TextService, Xingbian.Tsf, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null"),

    ("H1 Assembly value MISSING the 'Version=' segment",
     "Xingbian.Tsf.TextService, Xingbian.Tsf, Culture=neutral, PublicKeyToken=null"),

    ("H2 Assembly value present but BARE (no version/culture/token at all)",
     "Xingbian.Tsf.TextService, Xingbian.Tsf"),

    ("H3 Class written as short name only (no ', Assembly' part)",
     "Xingbian.Tsf.TextService"),

    ("H4 Class with WRONG namespace (Xingbian.TextService)",
     "Xingbian.TextService, Xingbian.Tsf, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null"),

    ("H5 Class that does not exist at all",
     "Xingbian.Tsf.NoSuchClass, Xingbian.Tsf, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null"),

    ("H6 Class with WRONG CASE",
     "xingbian.tsf.textservice, Xingbian.Tsf, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null"),

    ("H7 WRONG assembly name (Xingbian.Core)",
     "Xingbian.Tsf.TextService, Xingbian.Core, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null"),

    ("H8 WRONG version (9.9.9.9)",
     "Xingbian.Tsf.TextService, Xingbian.Tsf, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null"),

    ("H9 PublicKeyToken=null but assembly named as if strong-named",
     "Xingbian.Tsf.TextService, Xingbian.Tsf, Version=0.1.0.0, Culture=neutral, PublicKeyToken=1a2b3c4d5e6f7a8b"),
]


def main():
    print("=" * 78)
    print("clr_api_probe.py -- what does each malformed Class/Assembly value do?")
    print("=" * 78)
    print("interpreter : %s" % sys.executable)
    print("app base dir: %s" % os.path.dirname(sys.executable))
    print("bits        : %d" % (8 * ctypes.sizeof(c_void_p)))

    dll_here = os.path.join(os.path.dirname(sys.executable), "Xingbian.Tsf.dll")
    print("Xingbian.Tsf.dll in app base dir: %s"
          % ("EXISTS %d B" % os.path.getsize(dll_here) if os.path.isfile(dll_here)
             else "absent (CLR must then resolve some other way)"))

    print("\n--- the two registry values we are modelling (read-only) ---")
    reg = query_registry_values()
    for k in ("Class", "Assembly", "RuntimeVersion", "CodeBase"):
        print("    %-14s = %s" % (k, reg.get(k)))
    if reg.get("Class") and reg.get("Assembly"):
        print("    => mscoree concatenates these into:")
        print("       %s, %s" % (reg["Class"], reg["Assembly"]))

    print("\n--- resolving mscoree!ClrCreateManagedInstance ---")
    mscoree = ctypes.WinDLL(os.path.join(os.environ.get("SystemRoot", r"C:\Windows"),
                                         "System32", "mscoree.dll"))
    try:
        addr = ctypes.cast(mscoree.ClrCreateManagedInstance, c_void_p).value
    except AttributeError:
        print("    NOT EXPORTED -- cannot run this probe")
        return 2
    print("    mscoree!ClrCreateManagedInstance = 0x%016X" % addr)

    proto = ctypes.WINFUNCTYPE(c_int32, c_wchar_p, POINTER(GUID), POINTER(c_void_p))
    fn = proto(addr)
    iid = guid(IID_IUNKNOWN)

    print("\n" + "=" * 78)
    print("RESULTS")
    print("=" * 78)
    ok = 0
    for label, name in CASES:
        pp = c_void_p(0)
        hr = fn(name, byref(iid), byref(pp)) & 0xFFFFFFFF
        verdict = "OK  " if hr == 0 else "FAIL"
        if hr == 0:
            ok += 1
        print("%s  hr=0x%08X  %-52s" % (verdict, hr, hresult_name(hr)))
        print("      %s" % label)
        print("      \"%s\"" % name)
        if hr == 0 and pp.value:
            # prove it is really our object, not a stub
            vtbl = ctypes.cast(pp.value, POINTER(c_void_p))[0]
            print("      -> IUnknown* = 0x%X  (vtable at 0x%X)" % (pp.value, vtbl))
    print("\n%d of %d cases produced an object." % (ok, len(CASES)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
