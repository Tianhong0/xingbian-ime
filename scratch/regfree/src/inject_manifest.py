# -*- coding: utf-8 -*-
"""
inject_manifest.py -- embed an SxS assembly manifest INTO a copy of
Xingbian.Tsf.dll, then re-run the reg-free activation against it.

Rationale for the experiment
----------------------------
Our application manifest lists `<file name="Xingbian.Tsf.dll" />`.  In the SxS
model a `<file>` entry does not merely roster a path -- SxS expects that file to
BE a side-by-side assembly, identified by an assembly manifest embedded in it as
an RT_MANIFEST (resource type 24) resource.  Xingbian.Tsf.dll has no embedded
manifest at all (it is a plain class library), so activation reaches mscoree,
starts the CLR, and then fails to resolve anything: 0x80070002.

This script tests that hypothesis directly: copy the DLL, inject RT_MANIFEST
(id 1 and id 2 variants) carrying the assembly identity + <clrClass>, drop it in
a fresh directory, and activate an application manifest that references it.

Nothing here touches the registry or the original build output.
"""

import ctypes
import ctypes.wintypes as wintypes
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sxs_probe import (  # noqa: E402
    HERE, SCRATCH, APP_DIR, CLSID_TEXTSERVICE, IID_IUNKNOWN, GUID, guid, u32, hr_text,
)

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
ole32 = ctypes.WinDLL("ole32", use_last_error=True)

kernel32.BeginUpdateResourceW.argtypes = [wintypes.LPCWSTR, wintypes.BOOL]
kernel32.BeginUpdateResourceW.restype = wintypes.HANDLE
kernel32.UpdateResourceW.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                     wintypes.WORD, ctypes.c_void_p, wintypes.DWORD]
kernel32.UpdateResourceW.restype = wintypes.BOOL
kernel32.EndUpdateResourceW.argtypes = [wintypes.HANDLE, wintypes.BOOL]
kernel32.EndUpdateResourceW.restype = wintypes.BOOL
kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
kernel32.GetModuleHandleW.restype = wintypes.HMODULE

RT_MANIFEST = 24
CLSCTX_INPROC_SERVER = 1
ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID = 0x00000004


class ACTCTXW(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32),
                ("dwFlags", ctypes.c_uint32),
                ("lpSource", wintypes.LPCWSTR),
                ("wProcessorArchitecture", ctypes.c_uint16),
                ("wLangId", ctypes.c_uint16),
                ("lpAssemblyDirectory", wintypes.LPCWSTR),
                ("lpResourceName", wintypes.LPCWSTR),
                ("lpApplicationName", wintypes.LPCWSTR),
                ("hModule", wintypes.HMODULE)]


kernel32.CreateActCtxW.argtypes = [ctypes.POINTER(ACTCTXW)]
kernel32.CreateActCtxW.restype = wintypes.HANDLE
kernel32.ActivateActCtx.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_size_t)]
kernel32.ActivateActCtx.restype = wintypes.BOOL
ole32.CoInitializeEx.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
ole32.CoCreateInstance.argtypes = [ctypes.POINTER(GUID), ctypes.c_void_p,
                                   ctypes.c_uint32, ctypes.POINTER(GUID),
                                   ctypes.POINTER(ctypes.c_void_p)]

XMLDECL = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'

EMBEDDED_IDENTITY = "Xingbian.Tsf"


def embedded_manifest(dll_name):
    """The assembly manifest that lives INSIDE the DLL."""
    return (
        XMLDECL
        + '<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">\n'
        + '  <assemblyIdentity type="win32" name="%s" version="0.1.0.0"\n' % EMBEDDED_IDENTITY
        + '                    processorArchitecture="amd64" />\n'
        + '  <file name="%s" />\n' % dll_name
        + '  <file name="Xingbian.Core.dll" />\n'
        + '  <clrClass clsid="{%s}"\n' % CLSID_TEXTSERVICE
        + '            threadingModel="Both"\n'
        + '            runtimeVersion="v4.0.30319"\n'
        + '            name="Xingbian.Tsf.TextService, Xingbian.Tsf, Version=0.1.0.0,'
        + ' Culture=neutral, PublicKeyToken=null" />\n'
        + '</assembly>\n')


def app_manifest(dll_name):
    """The APPLICATION manifest: just depends on / rosters the assembly."""
    return (
        XMLDECL
        + '<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">\n'
        + '  <assemblyIdentity type="win32" name="Xingbian.RegFree.Probe" version="1.0.0.0"\n'
        + '                    processorArchitecture="amd64" />\n'
        + '  <file name="%s" />\n' % dll_name
        + '  <file name="Xingbian.Core.dll" />\n'
        + '</assembly>\n')


def inject(path, resource_id, text):
    data = text.encode("utf-16-le") + b"\x00\x00"
    h = kernel32.BeginUpdateResourceW(path, False)
    if not h:
        return None, ctypes.get_last_error()
    ok = kernel32.UpdateResourceW(h, ctypes.c_void_p(RT_MANIFEST),
                                  ctypes.c_void_p(resource_id), 0x0409,
                                  ctypes.c_char_p(data), len(data))
    if not ok:
        err = ctypes.get_last_error()
        kernel32.EndUpdateResourceW(h, True)
        return None, err
    ok = kernel32.EndUpdateResourceW(h, False)
    if not ok:
        return None, ctypes.get_last_error()
    return True, 0


def run_variant(dir_name, resource_id, tag):
    d = os.path.join(SCRATCH, dir_name)
    if os.path.isdir(d):
        shutil.rmtree(d)
    os.makedirs(d)
    src = os.path.join(APP_DIR, "Xingbian.Tsf.dll")
    dst = os.path.join(d, "Xingbian.Tsf.dll")
    shutil.copy2(src, dst)
    shutil.copy2(os.path.join(APP_DIR, "Xingbian.Core.dll"),
                 os.path.join(d, "Xingbian.Core.dll"))

    if resource_id is not None:
        ok, err = inject(dst, resource_id, embedded_manifest("Xingbian.Tsf.dll"))
        if not ok:
            print("  inject RT_MANIFEST id=%d FAILED GetLastError=%d" % (resource_id, err))
            return None
        print("  injected RT_MANIFEST id=%d (%d bytes) into %s"
              % (resource_id, len(embedded_manifest("Xingbian.Tsf.dll")), dst))
    else:
        print("  no embedded manifest (control)")

    mp = os.path.join(d, "app.manifest")
    with open(mp, "w", encoding="utf-8") as f:
        f.write(app_manifest("Xingbian.Tsf.dll"))

    ctx = ACTCTXW()
    ctx.cbSize = ctypes.sizeof(ACTCTXW)
    ctx.dwFlags = ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID
    ctx.lpSource = mp
    ctx.lpAssemblyDirectory = d
    h = kernel32.CreateActCtxW(ctypes.byref(ctx))
    if not h or h == wintypes.HANDLE(-1).value:
        print("  CreateActCtxW FAILED GetLastError=%d" % ctypes.get_last_error())
        return None
    cookie = ctypes.c_size_t(0)
    if not kernel32.ActivateActCtx(h, ctypes.byref(cookie)):
        print("  ActivateActCtx FAILED GetLastError=%d" % ctypes.get_last_error())
        return None

    g_clsid, g_iid = guid(CLSID_TEXTSERVICE), guid(IID_IUNKNOWN)
    out = ctypes.c_void_p(None)
    hr = ole32.CoCreateInstance(ctypes.byref(g_clsid), None, CLSCTX_INPROC_SERVER,
                                ctypes.byref(g_iid), ctypes.byref(out))
    print("  %-28s CoCreateInstance hr=0x%08X  %s   ptr=0x%X"
          % (tag, u32(hr), hr_text(hr), out.value or 0))
    for m in ("mscoree.dll", "clr.dll", "clrjit.dll"):
        print("      %-14s = 0x%X" % (m, kernel32.GetModuleHandleW(m) or 0))
    return u32(hr)


def main():
    print("=" * 78)
    print("inject_manifest.py -- does the DLL need its OWN embedded assembly manifest?")
    print("=" * 78)
    hr = ole32.CoInitializeEx(None, 2)
    print("CoInitializeEx hr=0x%08X" % u32(hr))
    print("")
    for dir_name, rid, tag in (("app_noembed", None, "control, no RT_MANIFEST"),
                               ("app_embed1", 1, "RT_MANIFEST id=1"),
                               ("app_embed2", 2, "RT_MANIFEST id=2")):
        print("[%s]" % dir_name)
        run_variant(dir_name, rid, tag)
        print("")
    return 0


if __name__ == "__main__":
    sys.exit(main())
