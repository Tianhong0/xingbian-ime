# -*- coding: utf-8 -*-
"""原始 vtable 取证 + 按槽真调（ground truth）。

用法：
    vt.py raw <iface>              原始 hexdump vtable 区域
    vt.py call <iface> <slot>      真调该槽（独立进程，崩溃不影响别轮）
    vt.py scan                     一次扫完所有槽（每个槽单独子进程不便，这里只在安全槽上用）

iface: u | b | e | k   (IUnknown / ITfTextInputProcessor / ITfTextInputProcessorEx / ITfKeyEventSink)
"""
import ctypes
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

CLSID = "{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}"
IFACES = {
    "u": ("IUnknown", "{00000000-0000-0000-C000-000000000046}", 3),
    "b": ("ITfTextInputProcessor", "{AA80E7F7-2021-11D2-93E0-0060B067B86E}", 5),
    "e": ("ITfTextInputProcessorEx", "{6E4E2102-F9CD-433D-B496-303CE03A6507}", 6),
    "k": ("ITfKeyEventSink", "{AA80E7F5-2021-11D2-93E0-0060B067B86E}", 9),
}
MK = ("QueryInterface", "AddRef", "Release", "Activate", "Deactivate", "ActivateEx",
      "OnSetFocus", "OnTestKeyDown", "OnTestKeyUp", "OnKeyDown", "OnKeyUp", "OnPreservedKey")


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
k32.ReadProcessMemory.restype = ctypes.c_int
k32.ReadProcessMemory.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                 ctypes.c_size_t, ctypes.c_void_p]
PROC = k32.GetCurrentProcess()


def rd(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t(0)
    if not k32.ReadProcessMemory(ctypes.c_void_p(PROC), ctypes.c_void_p(addr), buf, n,
                                 ctypes.byref(got)):
        return None
    return buf.raw[:got.value]


def make(key):
    _, iid_s, _ = IFACES[key]
    c, i = GUID(CLSID), GUID(iid_s)
    p = ctypes.c_void_p()
    h = ole32.CoCreateInstance(ctypes.byref(c), None, 0x5, ctypes.byref(i), ctypes.byref(p))
    return h, p


def slots_of(p, n):
    vt = ctypes.cast(p, ctypes.POINTER(ctypes.c_void_p))[0]
    arr = ctypes.cast(ctypes.c_void_p(vt), ctypes.POINTER(ctypes.c_void_p))
    return vt, [arr[i] for i in range(n)]


def cmd_raw(key):
    name, _, cnt = IFACES[key]
    h, p = make(key)
    print("=== raw %s ===" % name)
    print("CoCreateInstance hr=0x%08X ptr=0x%X" % (h & 0xFFFFFFFF, p.value or 0))
    if h or not p.value:
        return 1
    vt, vals = slots_of(p, cnt + 3)
    print("vtable @ 0x%X" % vt)
    print()
    print("--- 槽位值 ---")
    for i, v in enumerate(vals):
        tag = MK[i] if i < len(MK) else "slot%d" % i
        print("  [%d] %-18s 0x%016X" % (i, tag, v))
    print()
    print("--- vtable 原始字节 [vt-0x20, vt+0x60) ---")
    base = vt - 0x20
    raw = rd(base, 0x80)
    if raw is None:
        print("  读不到")
        return 1
    for off in range(0, 0x80, 16):
        chunk = raw[off:off + 16]
        hexs = " ".join("%02X" % c for c in chunk)
        qwords = struct.unpack_from("<QQ", chunk, 0)
        mark = ""
        a = base + off
        if vt <= a < vt + cnt * 8:
            mark = "  <- vtable 槽 %d" % ((a - vt) // 8)
        elif vt + cnt * 8 <= a < vt + cnt * 8 + 8:
            mark = "  <- 紧跟 vtable 之后"
        print("  0x%X: %-47s | %016X %016X%s" % (a, hexs, qwords[0], qwords[1], mark))
    return 0


def cmd_call(key, slot):
    name, _, cnt = IFACES[key]
    print("=== call %s slot %d ===" % (name, slot))
    h, p = make(key)
    print("CoCreateInstance hr=0x%08X ptr=0x%X" % (h & 0xFFFFFFFF, p.value or 0))
    if h or not p.value:
        return 1
    vt, vals = slots_of(p, cnt + 3)
    addr = vals[slot]
    tag = MK[slot] if slot < len(MK) else "slot%d" % slot
    print("槽 %d (%s) = 0x%X" % (slot, tag, addr))

    if slot in (4,):   # Deactivate(this)
        proto = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p)
        args = (ctypes.c_void_p(p.value),)
    elif slot == 3:    # Activate(this, ptim, tid)
        proto = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint)
        args = (ctypes.c_void_p(p.value), None, 0)
    elif slot == 5:    # ActivateEx(this, ptim, tid, flags)
        proto = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p,
                                   ctypes.c_uint, ctypes.c_uint)
        args = (ctypes.c_void_p(p.value), None, 0, 0)
    elif slot == 0:    # OnSetFocus(this, BOOL)  for k
        proto = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_int)
        args = (ctypes.c_void_p(p.value), 0)
    else:
        print("未实现该槽的调用签名")
        return 2

    fn = proto(addr)
    print("即将调用 ... (ptim=NULL)")
    sys.stdout.flush()
    r = fn(*args)
    print("返回 hr = 0x%08X" % (r & 0xFFFFFFFF))
    if (r & 0xFFFFFFFF) == 0x80070057:
        print("[ OK ] E_INVALIDARG -> 托管方法体确实通过该槽执行了")
    elif (r & 0xFFFFFFFF) == 0x00000000:
        print("[ OK ] S_OK")
    else:
        print("[ ?? ] 非预期返回值")
    return 0


if __name__ == "__main__":
    m = sys.argv[1]
    if m == "raw":
        sys.exit(cmd_raw(sys.argv[2]))
    if m == "call":
        sys.exit(cmd_call(sys.argv[2], int(sys.argv[3])))
    print("用法: vt.py raw|call")
    sys.exit(2)
