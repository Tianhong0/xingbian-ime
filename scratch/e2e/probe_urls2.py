# -*- coding: utf-8 -*-
"""Second wave of URL probing: find textstor.h / textstor.idl anywhere reachable."""
import sys

try:
    from urllib.request import urlopen, Request
except ImportError:
    from urllib2 import urlopen, Request

CANDIDATES = [
    # jsdelivr API: list the directory of the SDK repo
    "https://data.jsdelivr.com/v1/packages/gh/tpn/winsdk-10@master?structure=flat",
    # mingw-w64 headers (they carry textstor.idl)
    "https://cdn.jsdelivr.net/gh/mingw-w64/mingw-w64@master/mingw-w64-headers/include/textstor.idl",
    "https://cdn.jsdelivr.net/gh/mingw-w64/mingw-w64@master/mingw-w64-headers/include/textstor.h",
    # wine
    "https://cdn.jsdelivr.net/gh/wine-mirror/wine@master/include/textstor.idl",
    "https://cdn.jsdelivr.net/gh/wine-mirror/wine@master/dlls/msctf/textstor.c",
    # reactos
    "https://cdn.jsdelivr.net/gh/reactos/reactos@master/sdk/include/psdk/textstor.idl",
]

for url in CANDIDATES:
    try:
        req = Request(url, headers={"User-Agent": "python-urllib"})
        resp = urlopen(req, timeout=90)
        data = resp.read()
        sys.stdout.write("OK   %-110s bytes=%d\n" % (url, len(data)))
        name = url.split("/")[-1].split("?")[0]
        if name and "." in name:
            with open(name, "wb") as f:
                f.write(data)
            sys.stdout.write("     saved -> %s\n" % name)
    except Exception as exc:
        sys.stdout.write("ERR  %-110s %r\n" % (url, exc))
