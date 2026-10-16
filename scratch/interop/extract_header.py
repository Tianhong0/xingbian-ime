# -*- coding: utf-8 -*-
"""Dump, for every TSF interface in sdk_msctf.h:
   - the MIDL_INTERFACE IID
   - the ordered method list taken from the C++ class declaration (authoritative order,
     identical to the IXXXVtbl struct order) with raw C parameter declarations.
Used as an authoring aid; check_vtable.py independently re-derives the order from the
IXXXVtbl structs and compares it against the generated C#."""
import re, sys, io, json

HDR = r"D:\Documents\deepseek-harness\default-workspace\xingbian-ime\.ref\sdk_msctf.h"

with io.open(HDR, "r", encoding="utf-8", errors="replace") as f:
    text = f.read()

lines = text.split("\n")


def lineno(pos):
    return text.count("\n", 0, pos) + 1


# --- locate `MIDL_INTERFACE("...") NAME : public BASE { ... };` blocks -------------
blocks = []
for m in re.finditer(
    r'MIDL_INTERFACE\("([0-9a-fA-F\-]+)"\)\s*\n\s*([A-Za-z_][A-Za-z0-9_]*)\s*:\s*public\s+([A-Za-z_][A-Za-z0-9_]*)\s*\n?\s*\{',
    text,
):
    iid, name, base = m.group(1), m.group(2), m.group(3)
    # find matching closing brace of the class body
    i = text.index("{", m.end() - 1)
    depth = 0
    j = i
    while j < len(text):
        if text[j] == "{":
            depth += 1
        elif text[j] == "}":
            depth -= 1
            if depth == 0:
                break
        j += 1
    body = text[i + 1: j]
    blocks.append(
        dict(iid=iid.lower(), name=name, base=base, body=body,
             line=lineno(m.start()), endline=lineno(j))
    )


def parse_methods(body):
    """Return [(name, [param_decl,...]), ...] in declaration order."""
    out = []
    pat = re.compile(
        r'virtual\s+HRESULT\s+STDMETHODCALLTYPE\s+([A-Za-z_][A-Za-z0-9_]*)\s*\((.*?)\)\s*=\s*0\s*;',
        re.S,
    )
    for m in pat.finditer(body):
        out.append((m.group(1), m.group(2)))
    return out


def split_params(raw):
    """Split a C parameter list on top-level commas."""
    raw = raw.strip()
    if not raw or raw == "void":
        return []
    parts, depth, cur = [], 0, ""
    for ch in raw:
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append(cur)
            cur = ""
        else:
            cur += ch
    parts.append(cur)
    res = []
    for p in parts:
        p = " ".join(p.split())
        p = re.sub(r"/\*\s*\[[^\]]*\]\s*\*/", "", p).strip()
        p = p.replace("__RPC__in_opt", "").replace("__RPC__deref_out_opt", "")
        p = p.replace("__RPC__in", "").replace("__RPC__out", "").replace("__RPC__out_opt", "")
        p = " ".join(p.split())
        res.append(p)
    return res


results = []
for b in blocks:
    ms = parse_methods(b["body"])
    results.append(dict(
        name=b["name"], iid=b["iid"], base=b["base"], line=b["line"],
        methods=[dict(name=n, params=split_params(pr)) for n, pr in ms],
    ))

if len(sys.argv) > 1 and sys.argv[1] == "--json":
    print(json.dumps(results, indent=1, ensure_ascii=False))
    sys.exit(0)

want = sys.argv[1:] if len(sys.argv) > 1 else None
for r in results:
    if want and r["name"] not in want:
        continue
    print("")
    print("### %s  IID=%s  base=%s  (sdk_msctf.h:%d)  methods=%d"
          % (r["name"], r["iid"], r["base"], r["line"], len(r["methods"])))
    for i, m in enumerate(r["methods"]):
        print("   %2d %s(%s)" % (i, m["name"], ", ".join(m["params"])))
