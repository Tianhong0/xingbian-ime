# -*- coding: utf-8 -*-
"""字节级 vtable 取证：判断每个槽里到底是指向代码的桩，还是垃圾数据。

背景：`probe_vtable.py dump` 显示 ITfTextInputProcessorEx 的槽 5 = 0xfffffff015ff9090
（非规范地址）。若属实，TSF 调用 ActivateEx 会直接崩宿主。

但第一次的 VirtualQuery 判定写错了偏移（Protect 在 36 而不是 32，读到的是 State=0x1000
MEM_COMMIT），所以「不可执行」全部是误报。本脚本：
  1. 用正确的 MEMORY_BASIC_INFORMATION 布局重新判定每个槽的「是否已映射 / 是否可执行」；
  2. 把每个槽地址处的前 16 字节 dump 出来，按 x64 常见桩模式反汇编判断是什么；
  3. 顺带 dump vtable 自身所在内存，看槽位是否落在紧邻的区域。

结果写到 UTF-8 文件，避免控制台编码干扰。
"""

import ctypes
import struct
import sys

OUT = r"D:\Documents\deepseek-harness\default-workspace\xingbian-ime\scratch\vtable\dump.txt"
lines = []


def say(s=""):
    lines.append(s)


CLSID = "{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}"
IU = "{00000000-0000-0000-C000-000000000046}"
IB = "{AA80E7F7-2021-11D2-93E0-0060B067B86E}"
IE = "{6E4E2102-F9CD-433D-B496-303CE03A6507}"
IKS = "{AA80E7F5-2021-11D2-93E0-0060B067B86E}"


class GUID(ctypes.Structure):
    _fields_ = [("D1", ctypes.c_ulong), ("D2", ctypes.c_ushort),
                ("D3", ctypes.c_ushort), ("D4", ctypes.c_ubyte * 8)]

    def __init__(self, s=None):
        super(GUID, self).__init__()
        if s:
            ole32.CLSIDFromString(ctypes.c_wchar_p(s), ctypes.byref(self))


ole32 = ctypes.WinDLL("ole32")
k32 = ctypes.WinDLL("kernel32")

ole32.CoInitializeEx(None, 0x2)

k32.VirtualQuery.restype = ctypes.c_size_t
k32.VirtualQuery.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t]
k32.ReadProcessMemory.restype = ctypes.c_int
k32.ReadProcessMemory.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                 ctypes.c_size_t, ctypes.c_void_p]

PROC = k32.GetCurrentProcess()


def read_mem(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t(0)
    ok = k32.ReadProcessMemory(ctypes.c_void_p(PROC), ctypes.c_void_p(addr),
                               buf, n, ctypes.byref(got))
    if not ok:
        return None
    return buf.raw[:got.value]


def vq(addr):
    """返回 (state, protect, regionsize, type) 或 None。MEMORY_BASIC_INFORMATION (x64):"""
    if addr <= 0 or addr > 0x00007FFFFFFFFFFF:
        return None
    buf = ctypes.create_string_buffer(48)
    if k32.VirtualQuery(ctypes.c_void_p(addr), buf, 48) == 0:
        return None
    alloc_prot = struct.unpack_from("<I", buf.raw, 16)[0]
    region = struct.unpack_from("<Q", buf.raw, 24)[0]
    state = struct.unpack_from("<I", buf.raw, 32)[0]
    protect = struct.unpack_from("<I", buf.raw, 36)[0]
    typ = struct.unpack_from("<I", buf.raw, 40)[0]
    return state, protect, region, typ, alloc_prot


PROT = {
    0x01: "NOACCESS", 0x02: "R", 0x04: "RW", 0x08: "WC",
    0x10: "X", 0x20: "RX", 0x40: "RWX", 0x80: "WCX",
}
STATE = {0x1000: "MEM_COMMIT", 0x2000: "MEM_RESERVE", 0x10000: "MEM_FREE"}
TYPE = {0x1000000: "MEM_IMAGE", 0x40000: "MEM_MAPPED", 0x20000: "MEM_PRIVATE"}


def describe_addr(addr, depth=0):
    r = vq(addr)
    if r is None:
        return "未映射 / 非规范地址"
    state, protect, region, typ, aprot = r
    pname = PROT.get(protect & 0xFF, "0x%X" % protect)
    sname = STATE.get(state, "0x%X" % state)
    tname = TYPE.get(typ, "0x%X" % typ)
    return "%s %s %s region=0x%X" % (sname, pname, tname, region)


def decode_stub(addr):
    """读 16 字节并尝试识别 x64 桩。"""
    b = read_mem(addr, 16)
    if b is None:
        return None, "读不到内存"
    hx = " ".join("%02X" % c for c in b)
    note = []
    # E9 rel32  (jmp rel32)
    if b[0] == 0xE9:
        rel = struct.unpack_from("<i", b, 1)[0]
        tgt = addr + 5 + rel
        note.append("E9 jmp rel32 -> 0x%X" % tgt)
    # FF 25 disp32 (jmp qword ptr [rip+disp32])
    elif b[0] == 0xFF and b[1] == 0x25:
        disp = struct.unpack_from("<i", b, 2)[0]
        slot = addr + 6 + disp
        tp = read_mem(slot, 8)
        tgt = struct.unpack("<Q", tp)[0] if tp else None
        note.append("FF25 jmp [rip+%d] @0x%X" % (disp, slot))
        if tgt is not None:
            note.append("-> 0x%X" % tgt)
    # 48 FF 25 disp32 (REX.W jmp qword ptr [rip+disp32])
    elif b[0] == 0x48 and b[1] == 0xFF and b[2] == 0x25:
        disp = struct.unpack_from("<i", b, 3)[0]
        slot = addr + 7 + disp
        tp = read_mem(slot, 8)
        tgt = struct.unpack("<Q", tp)[0] if tp else None
        note.append("48FF25 jmp [rip+%d] @0x%X" % (disp, slot))
        if tgt is not None:
            note.append("-> 0x%X" % tgt)
    elif b[0] == 0x48 and b[1] == 0x8B and b[2] == 0xC4:
        note.append("48 8BC4 = mov rax,rsp (典型的 x64 函数序言/桩)")
    else:
        note.append("(未识别为常见桩形式)")
    return hx, "; ".join(note)


def make(iid):
    clsid = GUID(CLSID)
    i = GUID(iid)
    p = ctypes.c_void_p()
    h = ole32.CoCreateInstance(ctypes.byref(clsid), None, 0x5,
                               ctypes.byref(i), ctypes.byref(p))
    return h, p


def vtbl_slots(p, n):
    vt = ctypes.cast(p, ctypes.POINTER(ctypes.c_void_p))[0]
    arr = ctypes.cast(ctypes.c_void_p(vt), ctypes.POINTER(ctypes.c_void_p))
    return vt, [arr[i] for i in range(n)]


NAMES = {
    IU: (["QueryInterface", "AddRef", "Release"], 3),
    IB: (["QueryInterface", "AddRef", "Release", "Activate", "Deactivate"], 5),
    IE: (["QueryInterface", "AddRef", "Release", "Activate", "Deactivate", "ActivateEx"], 6),
    IKS: (["QueryInterface", "AddRef", "Release", "OnSetFocus", "OnTestKeyDown",
           "OnTestKeyUp", "OnKeyDown", "OnKeyUp", "OnPreservedKey"], 9),
}

say("=== 字节级 vtable 取证 ===")
say("CLSID = %s" % CLSID)
say("（MEMORY_BASIC_INFORMATION 偏移已修正：State@32 Protect@36 Type@40）")
say()

for iid, (names, cnt) in NAMES.items():
    h, p = make(iid)
    say("=" * 78)
    say("接口 %s  (期望槽数 = %d: IUnknown 3 + 方法 %d)" % (names[-1] if iid == IE else iid, cnt, cnt - 3))
    say("QI hr = 0x%08X , ptr = %s" % (h & 0xFFFFFFFF, hex(p.value or 0)))
    if h != 0 or not p.value:
        say("  (拿不到)")
        say()
        continue
    vt, slots = vtbl_slots(p, cnt + 2)  # 多读 2 个越界槽，用于对比
    say("vtable @ 0x%X   [%s]" % (vt, describe_addr(vt)))
    say()
    for i, s in enumerate(slots):
        tag = names[i] if i < len(names) else ("<越界槽 %d>" % i)
        mark = ""
        if i >= cnt:
            mark = "   <<< 超出接口方法数，本不该被调用"
        say("  [%d] %-18s = 0x%X%s" % (i, tag, s, mark))
        say("        %s" % describe_addr(s))
        hx, note = decode_stub(s)
        say("        bytes: %s" % hx)
        say("        %s" % note)
    say()

say("=" * 78)
say("判读要点")
say("  - 如果槽位指向的地址是 MEM_FREE 或非规范地址 -> 该槽从未被 CLR 填过 -> 调用必崩")
say("  - 如果槽位指向的字节不是任何跳转桩 -> 可能是数据而非代码")
say("  - 关键对比：IE 的槽 3/4 应当与 IB 的槽 3/4 行为一致（同一个 Activate/Deactivate）")

with open(OUT, "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + "\n")
print("written: " + OUT)
print("lines  : %d" % len(lines))
