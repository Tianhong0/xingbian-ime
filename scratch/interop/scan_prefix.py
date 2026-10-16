# -*- coding: utf-8 -*-
"""Byte-scan binaries for a GUID given by its first 4 bytes (Data1, little-endian).

Unlike probe_guids.py this does not require knowing the whole GUID: it only needs the
Data1 prefix, so it can confirm/discover the rest of a partially-remembered GUID.
"""
import sys, struct, os, glob

def prefix_bytes(d1_hex):
    return struct.pack("<I", int(d1_hex, 16))

def fmt(b):
    d1, d2, d3 = struct.unpack("<IHH", b[:8])
    d4 = b[8:]
    return "%08X-%04X-%04X-%s-%s" % (d1, d2, d3,
                                     d4[:2].hex().upper(), d4[2:].hex().upper())

def scan(path, pat, limit=200):
    hits = []
    try:
        with open(path, "rb") as f:
            blob = f.read()
    except OSError:
        return hits
    i = 0
    while len(hits) < limit:
        i = blob.find(pat, i)
        if i < 0:
            break
        g = blob[i:i + 16]
        if len(g) == 16:
            hits.append(fmt(g))
        i += 1
    return hits

def main():
    if len(sys.argv) < 2:
        print("usage: scan_prefix.py <Data1hex> [glob ...]")
        return 1
    pat = prefix_bytes(sys.argv[1])
    roots = sys.argv[2:] or [r"C:\Windows\System32\*.dll"]
    files = []
    for r in roots:
        files.extend(glob.glob(r))
    files.sort()
    seen = {}
    for p in files:
        for g in scan(p, pat):
            seen.setdefault(g, []).append(os.path.basename(p))
    if not seen:
        print("no 16-byte match for Data1=0x%s in %d files" % (sys.argv[1], len(files)))
    for g, where in sorted(seen.items()):
        print("  %s   %s" % (g, ",".join(sorted(set(where)))))
    return 0

if __name__ == "__main__":
    sys.exit(main())
