#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Find the namespace/schema in which the SxS parser accepts <clrClass>."""
import ctypes, os, sys
from ctypes import wintypes

HERE = os.path.dirname(os.path.abspath(__file__))
MAN = os.path.join(HERE, "probes2")
APP = os.path.join(os.path.dirname(HERE), "app")
ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID = 0x00000004

CLSID = "{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}"


class ACTCTXW(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32), ("dwFlags", ctypes.c_uint32),
                ("lpSource", wintypes.LPCWSTR), ("wProcessorArchitecture", ctypes.c_uint16),
                ("wLangId", ctypes.c_uint16), ("lpAssemblyDirectory", wintypes.LPCWSTR),
                ("lpResourceName", wintypes.LPCWSTR), ("lpApplicationName", wintypes.LPCWSTR),
                ("hModule", wintypes.HMODULE)]


k = ctypes.WinDLL("kernel32", use_last_error=True)
k.CreateActCtxW.argtypes = [ctypes.POINTER(ACTCTXW)]
# c_ssize_t (signed): a failed CreateActCtxW returns INVALID_HANDLE_VALUE == -1.
# Using c_void_p here would turn -1 into 0xFFFFFFFFFFFFFFFF (truthy) and silently
# turn every failure into a false "OK" -- which is exactly what happened once.
k.CreateActCtxW.restype = ctypes.c_ssize_t
k.ReleaseActCtx.argtypes = [ctypes.c_void_p]
INVALID = -1

XMLDECL = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'


def doc(ns, body, extra_root=""):
    return (XMLDECL
            + '<assembly xmlns="%s" manifestVersion="1.0"%s>\n' % (ns, extra_root)
            + '  <assemblyIdentity type="win32" name="Xingbian.RegFree.Probe" version="1.0.0.0"\n'
            + '                    processorArchitecture="amd64" />\n'
            + body
            + '</assembly>\n')


CLRCLASS = ('      <clrClass clsid="%s"\n'
            '              threadingModel="Both" name="Xingbian.Tsf.TextService"\n'
            '              runtimeVersion="v4.0.30319" />\n' % CLSID)
COMCLASS = ('      <comClass clsid="%s" threadingModel="Both"\n'
            '              description="Xingbian.Tsf.TextService" />\n' % CLSID)
FILE_OPEN = '  <file name="Xingbian.Tsf.dll">\n'
FILE_CLOSE = '  </file>\n'

V1 = "urn:schemas-microsoft-com:asm.v1"
V2 = "urn:schemas-microsoft-com:asm.v2"
V3 = "urn:schemas-microsoft-com:asm.v3"

PROBES = {
    # controls - establishes that the position itself is fine
    "C1_comClass_v1": doc(V1, FILE_OPEN + COMCLASS + FILE_CLOSE),
    "C2_empty_file_v1": doc(V1, FILE_OPEN + FILE_CLOSE),

    # namespace sweep for <clrClass> in the same position
    "N1_clrClass_v1": doc(V1, FILE_OPEN + CLRCLASS + FILE_CLOSE),
    "N2_clrClass_v2": doc(V2, FILE_OPEN + CLRCLASS + FILE_CLOSE),
    "N3_clrClass_v3": doc(V3, FILE_OPEN + CLRCLASS + FILE_CLOSE),

    # inherit v1 at the <assembly> level, redeclare v2 only on <clrClass>
    "N4_clrClass_ownns_v2": doc(V1, FILE_OPEN
                                + CLRCLASS.replace("<clrClass ",
                                                   '<clrClass xmlns="%s" ' % V2)
                                + FILE_CLOSE),

    # the well-known mixed-namespace idiom: asm.v1 root + asmv3:clrClass
    "N5_clrClass_prefixed_v3": (
        XMLDECL
        + '<assembly xmlns="%s" xmlns:asmv3="%s" manifestVersion="1.0">\n' % (V1, V3)
        + '  <assemblyIdentity type="win32" name="Xingbian.RegFree.Probe" version="1.0.0.0"\n'
        + '                    processorArchitecture="amd64" />\n'
        + '  <file name="Xingbian.Tsf.dll">\n'
        + '      <asmv3:clrClass clsid="%s"\n' % CLSID
        + '              threadingModel="Both" name="Xingbian.Tsf.TextService"\n'
        + '              runtimeVersion="v4.0.30319" />\n'
        + '  </file>\n'
        + '</assembly>\n'),

    # clrClass outside <file> (at assembly level)
    "N6_clrClass_toplevel_v1": doc(V1, CLRCLASS),

    # v1 root, clrClass at top level with explicit v3 namespace
    "N7_clrClass_toplevel_v3": doc(V3, CLRCLASS),
}


def try_manifest(name, xml):
    if not os.path.isdir(MAN):
        os.makedirs(MAN)
    p = os.path.join(MAN, name + ".manifest")
    with open(p, "w", encoding="utf-8") as f:
        f.write(xml)
    ctx = ACTCTXW()
    ctx.cbSize = ctypes.sizeof(ACTCTXW)
    ctx.dwFlags = ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID
    ctx.lpSource = p
    ctx.lpAssemblyDirectory = APP
    h = k.CreateActCtxW(ctypes.byref(ctx))
    err = ctypes.get_last_error()
    if h == INVALID or h <= 0:
        return None, err
    k.ReleaseActCtx(ctypes.c_void_p(h))
    return h, 0


def main():
    names = sys.argv[1:] or list(PROBES.keys())
    ok_count = 0
    for n in names:
        h, err = try_manifest(n, PROBES[n])
        if h:
            ok_count += 1
        print("%s %-26s handle=%-14s GetLastError=%d (0x%04X)  %s" %
              ("OK  " if h else "FAIL", n,
               ("0x%X" % h) if h else "-", err, err,
               {14001: "ERROR_SXS_CANT_GEN_ACTCTX",
                14002: "ERROR_SXS_ASSEMBLY_NOT_FOUND",
                14003: "ERROR_SXS_MANIFEST_FORMAT_ERROR",
                14004: "ERROR_SXS_MANIFEST_PARSE_ERROR",
                14108: "ERROR_SXS_INVALID_IDENTITY_ATTRIBUTE"}.get(err, "")))
    print("")
    print("summary: %d OK / %d FAIL out of %d" % (ok_count, len(names) - ok_count, len(names)))


if __name__ == "__main__":
    main()
