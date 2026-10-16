# -*- coding: utf-8 -*-
"""
record_dump.py -- decode the COM_SERVER_REGISTRATION record that SxS hands to
mscoree for our CLSID, including its UTF-16 string fields.

sxs_probe/deep_probe showed the record is 374 bytes and the ASCII scan found
nothing readable -- because the CLR assembly name is stored as UTF-16.  Decoding
it tells us EXACTLY what mscoree was told to load, which separates
"manifest is wrong" from "CLR simply cannot find the DLL".
"""

import ctypes
import ctypes.wintypes as wintypes
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sxs_probe import (  # noqa: E402
    APP_DIR, activate, guid, GUID, CLSID_TEXTSERVICE,
    ACTCTX_SECTION_KEYED_DATA, kernel32,
)

kernel32.FindActCtxSectionGuid.argtypes = [
    ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p), ctypes.c_uint32,
    ctypes.POINTER(GUID), ctypes.POINTER(ACTCTX_SECTION_KEYED_DATA)]
kernel32.FindActCtxSectionGuid.restype = wintypes.BOOL

XML = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
       '<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">\n'
       '  <assemblyIdentity type="win32" name="Xingbian.RegFree.Probe" version="1.0.0.0"\n'
       '                    processorArchitecture="amd64" />\n'
       '  <file name="Xingbian.Tsf.dll" />\n'
       '  <file name="Xingbian.Core.dll" />\n'
       '  <clrClass clsid="{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}"\n'
       '            threadingModel="Both"\n'
       '            runtimeVersion="v4.0.30319"\n'
       '            name="Xingbian.Tsf.TextService, Xingbian.Tsf, Version=0.1.0.0,'
       ' Culture=neutral, PublicKeyToken=null" />\n'
       '</assembly>\n')


def wide_strings(data):
    """Extract UTF-16LE runs of printable chars (len>=3) from a byte buffer.

    Scans BOTH byte alignments -- a single mis-aligned zero byte silently
    desynchronises a naive scanner and hides every string in the record.
    """
    out = []
    for align in (0, 1):
        i = align
        cur = []
        while i + 1 < len(data):
            ch = data[i] | (data[i + 1] << 8)
            if 32 <= ch < 0x7F:
                cur.append(chr(ch))
                i += 2
            else:
                if len(cur) >= 3:
                    out.append("".join(cur))
                cur = []
                i += 1
        if len(cur) >= 3:
            out.append("".join(cur))
    seen = set()
    uniq = []
    for s in out:
        if s not in seen:
            seen.add(s)
            uniq.append(s)
    return uniq


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    d = os.path.join(here, "probes_sxs")
    if not os.path.isdir(d):
        os.makedirs(d)
    p = os.path.join(d, "REC_asmqual_toplevel.manifest")
    with open(p, "w", encoding="utf-8") as f:
        f.write(XML)

    print("=" * 78)
    print("COM_SERVER_REGISTRATION record for CLSID %s" % CLSID_TEXTSERVICE)
    print("manifest: %s" % p)
    print("=" * 78)

    act, e = activate(p, APP_DIR)
    if act is None:
        print("activation failed: %s" % e)
        return 1

    g = guid(CLSID_TEXTSERVICE)
    dat = ACTCTX_SECTION_KEYED_DATA()
    dat.cbSize = ctypes.sizeof(ACTCTX_SECTION_KEYED_DATA)
    ok = kernel32.FindActCtxSectionGuid(0, None, 4, ctypes.byref(g), ctypes.byref(dat))
    if not ok:
        print("section 4 not found, err=%d" % ctypes.get_last_error())
        return 2

    raw = ctypes.string_at(dat.lpData, dat.ulLength)
    print("ulDataFormatVersion = %d" % dat.ulDataFormatVersion)
    print("ulLength            = %d" % dat.ulLength)
    print("hActCtx             = 0x%X" % (dat.hActCtx or 0))
    print("AssemblyMetadata.ulSectionTotalLength = %d" % dat.AssemblyMetadata.ulSectionTotalLength)
    print("AssemblyMetadata.ulAssemblyRosterIndex = %d" % dat.AssemblyMetadata.ulAssemblyRosterIndex)
    print("")
    print("--- full record as UTF-16 visible strings ---")
    for s in wide_strings(raw):
        print("    %r" % s)
    print("")
    print("--- full record hex ---")
    for off in range(0, len(raw), 16):
        chunk = raw[off:off + 16]
        print("    %04X  %-47s  %s" % (off,
                                       " ".join("%02X" % b for b in chunk),
                                       "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
