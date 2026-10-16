# -*- coding: utf-8 -*-
"""Verify TSF static GUIDs without uuid.lib.

Every TSF static GUID (EXTERN_C const GUID ...) is linked into msctf.dll's own data
section as a 16-byte little-endian GUID. So a *known-good* GUID (e.g. an interface IID
read straight out of sdk_msctf.h) MUST be findable in msctf.dll; that gives us a
positive control. We then probe a candidate value the same way.
This is supporting evidence only -- it cannot discover a wrong value, just confirm/deny.
"""
import io, struct, sys

DLLS = [
    r"C:\Windows\System32\msctf.dll",
    r"C:\Windows\System32\msutb.dll",
]

def guid_bytes(s):
    s = s.strip().strip("{}")
    p = s.split("-")
    d1 = int(p[0], 16); d2 = int(p[1], 16); d3 = int(p[2], 16)
    d4 = bytes.fromhex(p[3] + p[4])
    return struct.pack("<IHH", d1, d2, d3) + d4

def main():
    cands = []
    for a in sys.argv[1:]:
        cands.append(a)
    if not cands:
        print("usage: probe_guids.py {GUID} [{GUID} ...]")
        return 1
    blobs = []
    for d in DLLS:
        try:
            with open(d, "rb") as f:
                blobs.append((d, f.read()))
        except OSError as e:
            print("skip %s: %s" % (d, e))
    print("loaded %d binaries" % len(blobs))
    print("%-40s %-8s %s" % ("GUID", "hit", "where"))
    for c in cands:
        pat = guid_bytes(c)
        hits = []
        for name, blob in blobs:
            idx = blob.find(pat)
            if idx >= 0:
                # count occurrences
                n = 0; i = 0
                while True:
                    i = blob.find(pat, i)
                    if i < 0: break
                    n += 1; i += 1
                hits.append("%s x%d" % (name.split("\\")[-1], n))
        print("%-40s %-8s %s" % (c, "YES" if hits else "no", ", ".join(hits) or "-"))
    return 0

if __name__ == "__main__":
    sys.exit(main())
