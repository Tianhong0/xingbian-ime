# -*- coding: utf-8 -*-
"""
make_fixed.py -- build a PATCHED copy of Xingbian.Tsf in scratch, to test the fix
for the flattened-interface defect found in the CCW vtable.

The defect:  TsfInterop.cs declares
    public interface ITfTextInputProcessorEx : ITfTextInputProcessor
i.e. COM-style interface inheritance. The CLR does NOT flatten inherited
ComImport interfaces when it builds the CCW vtable -- it emits ONLY the derived
interface's own declared methods. So the vtable came out as

    slot[3] = ActivateEx        (should be Activate)
    slot[4] = clr.dll terminator (should be Deactivate)   <-- calling this faults
    slot[5] = garbage            (should be ActivateEx)   <-- TSF calls this

The fix is to FLATTEN the interface: declare Activate, Deactivate and ActivateEx
all on ITfTextInputProcessorEx, and drop the inheritance.

This script copies the real sources into scratch\regfree\fix\, applies that one
edit, and compiles with the in-box csc.exe. It never writes to src\.

Usage:  python make_fixed.py
Output: scratch\regfree\fix\Xingbian.Tsf.dll  (+ a diff summary on stdout)
"""

import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, ".."))
PROJECT = r"D:\Documents\deepseek-harness\default-workspace\xingbian-ime"
SRC_TSF = os.path.join(PROJECT, "src", "Xingbian.Tsf")
FIX_DIR = os.path.join(ROOT, "fix")
CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
REFDIR = r"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"

BASE_LIST = ["ITfTextInputProcessor", "ITfKeyEventSink", "ITfThreadMgrEventSink",
             "ITfEditSession"]


def copy_tree():
    if not os.path.isdir(FIX_DIR):
        os.makedirs(FIX_DIR)
    written = []
    for dirpath, dirnames, filenames in os.walk(SRC_TSF):
        rel = os.path.relpath(dirpath, SRC_TSF)
        if rel.startswith("bin") or rel.startswith("obj"):
            continue
        outdir = FIX_DIR if rel == "." else os.path.join(FIX_DIR, rel)
        if not os.path.isdir(outdir):
            os.makedirs(outdir)
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            src = os.path.join(dirpath, fn)
            dst = os.path.join(outdir, fn)
            with open(src, "rb") as f:
                data = f.read()
            with open(dst, "wb") as f:
                f.write(data)
            written.append(dst)
    return written


def patch_interop(files):
    """Flatten ITfTextInputProcessorEx; return a description of what changed."""
    target = None
    for f in files:
        if f.endswith("TsfInterop.cs"):
            target = f
            break
    if target is None:
        raise SystemExit("TsfInterop.cs not found in the copied tree")

    with open(target, "rb") as fh:
        text = fh.read().decode("utf-8")

    # 1) drop the base interface from the declaration
    old_decl = "public interface ITfTextInputProcessorEx : ITfTextInputProcessor"
    new_decl = "public interface ITfTextInputProcessorEx"
    if old_decl not in text:
        raise SystemExit("declaration not found verbatim:\n  " + old_decl)
    text = text.replace(old_decl, new_decl, 1)

    # 2) insert the two inherited methods above ActivateEx
    anchor = "[PreserveSig]\r\n        int ActivateEx(" if "\r\n" in text \
        else "[PreserveSig]\n        int ActivateEx("
    if anchor not in text:
        m = re.search(r"\[PreserveSig\]\s*\n\s*int ActivateEx\(", text)
        if not m:
            raise SystemExit("ActivateEx declaration not found")
        anchor = m.group(0)
    spacer = "\r\n        " if "\r\n" in text else "\n        "
    addition = (
        "[PreserveSig]" + spacer +
        "int Activate([MarshalAs(UnmanagedType.Interface)] ITfThreadMgr ptim, uint tid);" + spacer +
        "// slot 1" + spacer +
        "[PreserveSig]" + spacer +
        "int Deactivate();" + spacer +
        "// slot 2" + spacer
    )
    text = text.replace(anchor, addition + anchor, 1)

    with open(target, "wb") as fh:
        fh.write(text.encode("utf-8"))
    return target


def patch_textservice(files):
    """TextService must now name ITfTextInputProcessor explicitly, since
    ITfTextInputProcessorEx no longer inherits it."""
    target = None
    for f in files:
        if f.endswith("TextService.cs"):
            target = f
            break
    if target is None:
        return None
    with open(target, "rb") as fh:
        text = fh.read().decode("utf-8")
    # find the interface list of the class declaration
    m = re.search(r"public sealed class TextService\s*:\s*((?:\s*[\w\.]+\s*,\s*)*[\w\.]+)",
                  text)
    if not m:
        return None
    if "ITfTextInputProcessor," in m.group(1) or \
       m.group(1).strip().startswith("ITfTextInputProcessor,"):
        return target  # already present
    new_list = "ITfTextInputProcessor," + m.group(1)
    text = text[:m.start(1)] + new_list + text[m.end(1):]
    with open(target, "wb") as fh:
        fh.write(text.encode("utf-8"))
    return target


def build(files, out_dll):
    core = os.path.join(PROJECT, "src", "Xingbian.Tsf", "bin", "Release",
                        "Xingbian.Core.dll")
    refs = [
        "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll",
        "/r:System.Windows.Forms.dll", "/r:System.Xml.dll",
        "/r:" + core,
    ]
    for extra in ("System.Runtime.InteropServices.dll",):
        p = os.path.join(REFDIR, extra)
        if os.path.exists(p):
            refs.append("/r:" + p)
    cmd = [CSC, "/nologo", "/target:library", "/langversion:7.3",
           "/platform:x64", "/optimize+", "/out:" + out_dll] + refs + files
    print("$ " + " ".join('"%s"' % c if " " in c else c for c in cmd))
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out, _ = p.communicate()
    txt = out.decode("utf-8", "replace")
    if txt.strip():
        print(txt)
    return p.returncode


def main():
    print("=" * 78)
    print("make_fixed.py -- patched build of Xingbian.Tsf (flattened interfaces)")
    print("=" * 78)
    files = copy_tree()
    print("copied %d .cs files into %s" % (len(files), FIX_DIR))

    t = patch_interop(files)
    print("patched : %s   (ITfTextInputProcessorEx flattened)" % t)
    t2 = patch_textservice(files)
    print("patched : %s   (TextService implements ITfTextInputProcessor directly)"
          % t2)

    out_dll = os.path.join(FIX_DIR, "Xingbian.Tsf.dll")
    rc = build(files, out_dll)
    print("[csc exit code] %d" % rc)
    if rc == 0 and os.path.exists(out_dll):
        print("BUILT: %s  (%d bytes)" % (out_dll, os.path.getsize(out_dll)))
        # copy the dependency so the fixed folder is self-contained
        core = os.path.join(PROJECT, "src", "Xingbian.Tsf", "bin", "Release",
                            "Xingbian.Core.dll")
        dst = os.path.join(FIX_DIR, "Xingbian.Core.dll")
        with open(core, "rb") as a, open(dst, "wb") as b:
            b.write(a.read())
        print("copied Xingbian.Core.dll alongside it")
    return rc


if __name__ == "__main__":
    sys.exit(main())
