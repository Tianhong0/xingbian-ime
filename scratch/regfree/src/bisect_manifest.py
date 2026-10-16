#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Bisect which manifest construct makes CreateActCtxW fail with 14001."""
import ctypes, os, sys
from ctypes import wintypes

MAN = os.path.join(os.path.dirname(os.path.abspath(__file__)), "probes")
APP = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "app")

ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID = 0x00000004


class ACTCTXW(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32), ("dwFlags", ctypes.c_uint32),
                ("lpSource", wintypes.LPCWSTR), ("wProcessorArchitecture", ctypes.c_uint16),
                ("wLangId", ctypes.c_uint16), ("lpAssemblyDirectory", wintypes.LPCWSTR),
                ("lpResourceName", wintypes.LPCWSTR), ("lpApplicationName", wintypes.LPCWSTR),
                ("hModule", wintypes.HMODULE)]


k = ctypes.WinDLL("kernel32", use_last_error=True)
k.CreateActCtxW.argtypes = [ctypes.POINTER(ACTCTXW)]
k.CreateActCtxW.restype = ctypes.c_void_p
k.ReleaseActCtx.argtypes = [ctypes.c_void_p]
INVALID = wintypes.HANDLE(-1).value

HEAD = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
IDENT = ('<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">\n'
         '  <assemblyIdentity type="win32" name="Xingbian.RegFree.Probe" version="1.0.0.0"\n'
         '                    processorArchitecture="amd64" />\n')
TAIL = '</assembly>\n'

CLRCLASS = ('      <clrClass clsid="{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}"\n'
            '              threadingModel="Both" name="Xingbian.Tsf.TextService"\n'
            '              runtimeVersion="v4.0.30319" />\n')

PROBES = {
    "P0_identity_only":      HEAD + IDENT + TAIL,
    "P1_file_no_clrclass":   HEAD + IDENT + '  <file name="Xingbian.Tsf.dll" />\n' + TAIL,
    "P2_file_and_clrclass":  HEAD + IDENT + '  <file name="Xingbian.Tsf.dll">\n' + CLRCLASS + '  </file>\n' + TAIL,
    "P3_clrclass_selfclose": HEAD + IDENT + '  <file name="Xingbian.Tsf.dll">\n' + CLRCLASS + '  </file>\n'
                             + '  <file name="Xingbian.Core.dll" />\n' + TAIL,
    "P4_clrclass_no_threading": HEAD + IDENT + '  <file name="Xingbian.Tsf.dll">\n'
                                + '      <clrClass clsid="{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}" '
                                + 'name="Xingbian.Tsf.TextService" runtimeVersion="v4.0.30319" />\n'
                                + '  </file>\n' + TAIL,
    "P5_clrclass_only_clsid": HEAD + IDENT + '  <file name="Xingbian.Tsf.dll">\n'
                              + '      <clrClass clsid="{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}" />\n'
                              + '  </file>\n' + TAIL,
    "P6_clrclass_runtimeOnly": HEAD + IDENT + '  <file name="Xingbian.Tsf.dll">\n'
                               + '      <clrClass name="Xingbian.Tsf.TextService" runtimeVersion="v4.0.30319" />\n'
                               + '  </file>\n' + TAIL,
    "P7_file_missing_dll":   HEAD + IDENT + '  <file name="NoSuchFile.dll">\n' + CLRCLASS + '  </file>\n' + TAIL,
    "P8_no_xml_decl":        IDENT + '  <file name="Xingbian.Tsf.dll">\n' + CLRCLASS + '  </file>\n' + TAIL,
    "P9_asmv3_ns":           '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
                             + '<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">\n'
                             + '  <assemblyIdentity type="win32" name="Xingbian.RegFree.Probe" version="1.0.0.0"\n'
                             + '                    processorArchitecture="amd64" />\n'
                             + '  <file name="Xingbian.Tsf.dll">\n' + CLRCLASS + '  </file>\n' + TAIL,
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
    if h == INVALID or not h:
        return p, None, err
    k.ReleaseActCtx(h)
    return p, h, 0


def main():
    names = sys.argv[1:] or list(PROBES.keys())
    for n in names:
        p, h, err = try_manifest(n, PROBES[n])
        flag = "OK  " if h else "FAIL"
        print("%s %-26s handle=%-8s GetLastError=%d (0x%04X)" %
              (flag, n, ("0x%X" % h) if h else "-", err, err))


if __name__ == "__main__":
    main()
