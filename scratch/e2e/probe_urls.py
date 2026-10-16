# -*- coding: utf-8 -*-
"""Probe several candidate URLs for the SDK headers that define ITextStoreACP."""
import sys

try:
    from urllib.request import urlopen, Request
except ImportError:
    from urllib2 import urlopen, Request

CANDIDATES = [
    "https://cdn.jsdelivr.net/gh/tpn/winsdk-10@master/Include/10.0.16299.0/um/msctf.h",
    "https://cdn.jsdelivr.net/gh/tpn/winsdk-10@master/Include/10.0.16299.0/um/textstor.h",
    "https://cdn.jsdelivr.net/gh/tpn/winsdk-10@master/Include/10.0.16299.0/shared/textstor.h",
    "https://cdn.jsdelivr.net/gh/tpn/winsdk-10@master/Include/10.0.16299.0/shared/textstor.idl",
    "https://cdn.jsdelivr.net/gh/tpn/winsdk-10@master/Include/10.0.16299.0/um/textstor.idl",
    "https://raw.githubusercontent.com/tpn/winsdk-10/master/Include/10.0.16299.0/shared/textstor.h",
    "https://raw.githubusercontent.com/tpn/winsdk-10/master/Include/10.0.16299.0/um/msctf.h",
]

for url in CANDIDATES:
    try:
        req = Request(url, headers={"User-Agent": "python-urllib"})
        resp = urlopen(req, timeout=60)
        data = resp.read()
        sys.stdout.write("OK   %-100s bytes=%d\n" % (url, len(data)))
    except Exception as exc:
        sys.stdout.write("ERR  %-100s %r\n" % (url, exc))
