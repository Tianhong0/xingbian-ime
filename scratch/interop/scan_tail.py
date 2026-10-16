# -*- coding: utf-8 -*-
"""Find every 16-byte GUID blob in a set of binaries that ends with a given Data4 tail."""
import sys, struct, glob, os, collections


def fmt(b):
    d1, d2, d3 = struct.unpack("<IHH", b[:8])
    d4 = b[8:]
    return "%08X-%04X-%04X-%s-%s" % (d1, d2, d3,
                                     d4[:2].hex().upper(), d4[2:].hex().upper())


def main():
    tail_hex = sys.argv[1].replace("-", "").upper()
    patterns = sys.argv[2:] or [r"C:\Windows\System32\*.dll"]
    tail = bytes.fromhex(tail_hex)
    files = []
    for p in patterns:
        files.extend(glob.glob(p))
    files.sort()
    seen = collections.OrderedDict()
    for path in files:
        try:
            with open(path, "rb") as f:
                blob = f.read()
        except OSError:
            continue
        i = 0
        while True:
            i = blob.find(tail, i)
            if i < 0:
                break
            start = i - (16 - len(tail))
            if start >= 0:
                g = blob[start:start + 16]
                if g[16 - len(tail):].hex().upper() == tail_hex:
                    seen.setdefault(fmt(g), []).append(os.path.basename(path))
            i += 1
    print("tail=%s  scanned %d files  -> %d distinct GUIDs" % (tail_hex, len(files), len(seen)))
    for g, w in seen.items():
        print("  %s   %s" % (g, ",".join(sorted(set(w)))))
    return 0


if __name__ == "__main__":
    sys.exit(main())
