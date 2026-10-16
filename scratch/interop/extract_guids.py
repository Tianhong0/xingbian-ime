# -*- coding: utf-8 -*-
"""Extract every GUID and every interface signature we need out of the
authoritative Windows SDK headers in .ref/, so the C# COM interop layer is
not written from memory.

Outputs (all under scratch/interop/):
  guids.txt   NAME<TAB>{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}
  sigs.txt    "<InterfaceName>.<MethodName>(<params>) -> <ret>"
"""
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REF = os.path.join(ROOT, ".ref")
OUT = os.path.join(ROOT, "scratch", "interop")

HDR = os.path.join(REF, "sdk_msctf.h")
IDL = os.path.join(REF, "sdk_msctf.idl")

# ------------------------------------------------------------------ GUIDs
GUID_RE = re.compile(
    r"(\b\w+)\s*,\s*"
    r"0x([0-9a-fA-F]{1,8})\s*,\s*0x([0-9a-fA-F]{1,4})\s*,\s*0x([0-9a-fA-F]{1,4})\s*,\s*"
    r"0x([0-9a-fA-F]{1,2})\s*,\s*0x([0-9a-fA-F]{1,2})\s*,\s*"
    r"0x([0-9a-fA-F]{1,2})\s*,\s*0x([0-9a-fA-F]{1,2})\s*,\s*"
    r"0x([0-9a-fA-F]{1,2})\s*,\s*0x([0-9a-fA-F]{1,2})\s*,\s*"
    r"0x([0-9a-fA-F]{1,2})\s*,\s*0x([0-9a-fA-F]{1,2})\s*\)"
)


def fmt(g):
    p = [int(x, 16) for x in g]
    return "{%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}" % (
        p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7], p[8], p[9], p[10])


def extract_guids(text):
    found = {}
    for m in GUID_RE.finditer(text):
        name = m.group(1)
        if name.startswith("0x"):
            continue
        found[name] = fmt(m.groups()[1:])
    return found


guids = {}
with open(HDR, "r", encoding="utf-8", errors="replace") as f:
    guids.update(extract_guids(f.read()))

# msctf.h only; also try the raw msctf_h dump and the idl for extras
for extra in ("msctf_h", "sdk_msctf.idl", "msctf.idl"):
    p = os.path.join(REF, extra)
    if not os.path.exists(p):
        continue
    with open(p, "r", encoding="utf-8", errors="replace") as f:
        for k, v in extract_guids(f.read()).items():
            guids.setdefault(k, v)

with open(os.path.join(OUT, "guids.txt"), "w", encoding="utf-8") as f:
    for k in sorted(guids):
        f.write("%s\t%s\n" % (k, guids[k]))
print("guids: %d written" % len(guids))

# ------------------------------------------------------- interface signatures
# Parse the MIDL interface blocks out of sdk_msctf.idl.
IDL_START = re.compile(r"^\s*interface\s+(?P<name>\w+)\s*:\s*(?P<base>\w+)\s*$", re.M)

text = open(IDL, "r", encoding="utf-8", errors="replace").read()

# MIDL style:  HRESULT Name(\n [in] TYPE param,\n ... );
CALL_RE = re.compile(r"HRESULT\s+(\w+)\s*\((.*?)\)\s*;", re.S)
PARAM_RE = re.compile(r"\[[^\]]*\]\s*([^,)]+)", re.S)


def norm_params(body):
    body = " ".join(body.split())
    if not body.strip() or body.strip() == "void":
        return ""
    # split on commas that are not inside brackets/parens
    out, depth, cur = [], 0, ""
    for ch in body:
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        if ch == "," and depth == 0:
            out.append(cur)
            cur = ""
        else:
            cur += ch
    out.append(cur)
    cleaned = []
    for p in out:
        p = re.sub(r"^\[[^\]]*\]\s*", "", " ".join(p.split()))
        p = p.replace(" *", "*").replace("* ", "*")
        cleaned.append(p)
    return ", ".join(cleaned)


# find each interface block: from "interface X : Y" to the next "interface " at col 0
blocks = []
for m in re.finditer(r"interface\s+(\w+)\s*:\s*(\w+)\s*\n?\{", text):
    start = m.end() - 1
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                blocks.append((m.group(1), m.group(2), text[start:i + 1]))
                break

sig_lines = []
sigs = {}
for name, base, body in blocks:
    for cm in CALL_RE.finditer(body):
        meth = cm.group(1)
        params = norm_params(cm.group(2))
        sigs.setdefault(name, []).append((meth, params))
        sig_lines.append("%s.%s(%s)" % (name, meth, params))

with open(os.path.join(OUT, "sigs.txt"), "w", encoding="utf-8") as f:
    f.write("\n".join(sig_lines))
print("interfaces: %d, methods: %d" % (len(sigs), len(sig_lines)))

# uuid per interface from the [uuid(...)] attribute preceding the declaration
iface_uuid = {}
for m in re.finditer(r"uuid\(\s*([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\)"
                     r"[^;{]*?\binterface\s+(\w+)", text, re.S):
    iface_uuid[m.group(2)] = "{%s}" % m.group(1).upper()
with open(os.path.join(OUT, "iface_uuid.txt"), "w", encoding="utf-8") as f:
    for k in sorted(iface_uuid):
        f.write("%s\t%s\n" % (k, iface_uuid[k]))
print("iface uuids from idl: %d" % len(iface_uuid))

# also try vendor uuid attribute form: [uuid(....), ...] \n interface X
for m in re.finditer(r"\[\s*(?:object\s*,\s*)?uuid\(\s*([0-9a-fA-F-]{36})\s*\)[^\]]*\]\s*"
                     r"(?:\n\s*)*interface\s+(\w+)", text):
    iface_uuid.setdefault(m.group(2), "{%s}" % m.group(1).upper())
with open(os.path.join(OUT, "iface_uuid.txt"), "w", encoding="utf-8") as f:
    for k in sorted(iface_uuid):
        f.write("%s\t%s\n" % (k, iface_uuid[k]))
print("iface uuids total: %d" % len(iface_uuid))
