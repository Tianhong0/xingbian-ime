# -*- coding: utf-8 -*-
"""Download the authoritative Windows SDK headers that define ITextStoreACP
and print the vtbl method order found in them.

curl.exe is unusable on this machine, so we use urllib.
"""
import io
import os
import sys
import urllib.request

try:
    from urllib.request import urlopen
except ImportError:
    from urllib2 import urlopen

BASE = "https://cdn.jsdelivr.net/gh/tpn/winsdk-10@master/Include/10.0.16299.0/um/"
FILES = ["textstor.h", "textstor.idl"]
OUT = os.path.dirname(os.path.abspath(__file__))


def fetch(name):
    url = BASE + name
    sys.stderr.write("fetching %s\n" % url)
    data = urlopen(url, timeout=60).read()
    path = os.path.join(OUT, name)
    with open(path, "wb") as f:
        f.write(data)
    sys.stderr.write("  wrote %s (%d bytes)\n" % (path, len(data)))
    return path


def main():
    paths = {}
    for name in FILES:
        try:
            paths[name] = fetch(name)
        except Exception as exc:
            sys.stderr.write("  FAILED %s: %r\n" % (name, exc))
    for name, path in paths.items():
        sys.stderr.write("OK %s -> %s\n" % (name, path))


if __name__ == "__main__":
    main()
