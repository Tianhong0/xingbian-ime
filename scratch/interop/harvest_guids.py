# -*- coding: utf-8 -*-
"""Harvest the TSF static GUIDs straight out of msctf.dll.

msctf.h/msctf.idl only *declare* the static GUIDs (EXTERN_C const GUID ...); the values
live in the SDK's uuid.lib, which this machine does not have (no Windows Kits, no MSVC).
But msctf.dll references all of them from its own data section, so every value is present
as a 16-byte little-endian blob. TSF GUIDs overwhelmingly share the Microsoft data4 tails
"00C04F8ECE14" and "00065B84435C", so we locate those tails and print the 16 bytes before.

Usage: harvest_guids.py [dll ...]
"""
import sys, struct, collections

TAILS = {
    "00C04F8ECE14": "ctf-legacy",
    "00065B84435C": "ctf-2000",
    "00105A2799B5": "ctf-core",
    "0060B067B86E": "ctf-11d2",
    "B0D0F0D0B6E2": "?",
}


def fmt(b):
    d1, d2, d3 = struct.unpack("<IHH", b[:8])
    d4 = b[8:]
    return "%08X-%04X-%04X-%s-%s" % (
        d1, d2, d3,
        d4[:2].hex().upper(), d4[2:].hex().upper())


def scan(path):
    with open(path, "rb") as f:
        blob = f.read()
    found = collections.OrderedDict()
    for tail_hex, fam in TAILS.items():
        tail = bytes.fromhex(tail_hex)
        i = 0
        while True:
            i = blob.find(tail, i)
            if i < 0:
                break
            start = i - 10            # 6 bytes of data4 already matched -> 10 bytes precede
            if start >= 0:
                g = blob[start:start + 16]
                if g[10:].hex().upper() == tail_hex:
                    found.setdefault(fmt(g), (fam, start))
            i += 1
    return found


def main():
    paths = sys.argv[1:] or [r"C:\Windows\System32\msctf.dll"]
    for p in paths:
        print("=== %s ===" % p)
        f = scan(p)
        for g, (fam, off) in f.items():
            print("  %s   [%s @0x%X]" % (g, fam, off))
        print("  total %d distinct" % len(f))
    return 0


if __name__ == "__main__":
    sys.exit(main())
