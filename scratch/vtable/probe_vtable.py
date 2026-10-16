# -*- coding: utf-8 -*-
"""原生探针：直接读我们 CCW 的 vtable，并按槽位真调函数。

为什么要做：regfree 子代理报告 ITfTextInputProcessorEx 的 vtable 槽 5
（ActivateEx）是 0xFFFFFFF015FF9090，调用即崩。若为真，TSF 激活时会调
ActivateEx -> 宿主崩溃。这是致命缺陷，必须自己证实或证伪。

本脚本用 ctypes 直接走 ole32!CoCreateInstance 拿到真实的原生接口指针，
不做任何托管包装，因此读到/调到的就是 TSF 会看到/调到的同一份 vtable。

用法：
    probe_vtable.py dump          只读 vtable，不调用任何槽（安全）
    probe_vtable.py call <slot>   调用指定槽（可能在原生层崩溃，故意分开跑）

ITfTextInputProcessorEx 的约定布局（Native 侧，x64）：
    0 QueryInterface   1 AddRef   2 Release
    3 ITfTextInputProcessor::Activate(ITfThreadMgr*, TfClientId)
    4 ITfTextInputProcessor::Deactivate()
    5 ITfTextInputProcessorEx::ActivateEx(ITfThreadMgr*, TfClientId, DWORD)
"""

import ctypes
import sys
from ctypes import wintypes

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

CLSID = "{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}"
IID_IUnknown = "{00000000-0000-0000-C000-000000000046}"
IID_ITfTextInputProcessor = "{AA80E7F7-2021-11D2-93E0-0060B067B86E}"
IID_ITfTextInputProcessorEx = "{6E4E2102-F9CD-433D-B496-303CE03A6507}"

CLSCTX_INPROC_SERVER = 0x1
CLSCTX_LOCAL_SERVER = 0x4


class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", ctypes.c_ulong),
        ("Data2", ctypes.c_ushort),
        ("Data3", ctypes.c_ushort),
        ("Data4", ctypes.c_ubyte * 8),
    ]

    def __init__(self, s=None):
        super(GUID, self).__init__()
        if s:
            ole32.CLSIDFromString(ctypes.c_wchar_p(s), ctypes.byref(self))


ole32 = ctypes.WinDLL("ole32")
K32 = ctypes.WinDLL("kernel32")

# CoCreateInstance 前必须初始化 COM，否则返回 0x800401F0 CO_E_NOTINITIALIZED。
# 用 COINIT_APARTMENTTHREADED = 0x2（TSF 是 STA 世界）。
COINIT_APARTMENTTHREADED = 0x2
_COM_HR = ole32.CoInitializeEx(None, COINIT_APARTMENTTHREADED)
print("[init] CoInitializeEx(STA) -> 0x%08X" % (_COM_HR & 0xFFFFFFFF))


def hr(x):
    return "0x%08X" % (x & 0xFFFFFFFF)


def create(iid_str):
    clsid = GUID(CLSID)
    iid = GUID(iid_str)
    p = ctypes.c_void_p()
    h = ole32.CoCreateInstance(
        ctypes.byref(clsid), None,
        CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER,
        ctypes.byref(iid), ctypes.byref(p),
    )
    return h, p


def vtable_of(p, n=8):
    if not p or not p.value:
        return []
    vtbl = ctypes.cast(p, ctypes.POINTER(ctypes.c_void_p))[0]
    if not vtbl:
        return []
    arr = ctypes.cast(ctypes.c_void_p(vtbl), ctypes.POINTER(ctypes.c_void_p))
    return [arr[i] for i in range(n)]


def dump():
    print("=== 原生 vtable dump（ctypes -> ole32!CoCreateInstance）===")
    print("CLSID = %s" % CLSID)
    print()

    results = {}
    for label, iid in [
        ("IUnknown", IID_IUnknown),
        ("ITfTextInputProcessor", IID_ITfTextInputProcessor),
        ("ITfTextInputProcessorEx", IID_ITfTextInputProcessorEx),
    ]:
        h, p = create(iid)
        ptr = p.value if p else None
        print("--- QI %s ---" % label)
        print("    hr=%s ptr=%s" % (hr(h), hex(ptr) if ptr else "None"))
        if h != 0 or not ptr:
            print("    (拿不到接口指针)")
            print()
            continue
        slots = vtable_of(p)
        vtbl = ctypes.cast(p, ctypes.POINTER(ctypes.c_void_p))[0]
        print("    vtable @ %s" % hex(vtbl))
        for i, s in enumerate(slots):
            tag = ""
            if i == 0:
                tag = "QueryInterface"
            elif i == 1:
                tag = "AddRef"
            elif i == 2:
                tag = "Release"
            elif i == 3:
                tag = "Activate"
            elif i == 4:
                tag = "Deactivate"
            elif i == 5:
                tag = "ActivateEx"
            note = ""
            if s:
                # 判断是否是可执行的规范地址（x64 用户态：高位全 0，且低 48 位有效）
                if s > 0x00007FFFFFFFFFFF:
                    note = "  <== 非规范地址/明显非法！"
                else:
                    mi = ctypes.c_void_p()
                    k32 = ctypes.WinDLL("kernel32")
                    k32.VirtualQuery.restype = ctypes.c_size_t
                    k32.VirtualQuery.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t]
                    buf = ctypes.create_string_buffer(48)
                    got = k32.VirtualQuery(ctypes.c_void_p(s), buf, 48)
                    if got == 0:
                        note = "  <== VirtualQuery 失败，未映射！"
                    else:
                        prot = ctypes.c_ulong.from_buffer(buf, 32).value
                        if not (prot & 0x10 or prot & 0x20 or prot & 0x40):
                            note = "  <== 该地址不可执行 (prot=0x%X)！" % prot
            print("    [%d] %-18s %s%s" % (i, tag, hex(s), note))
        results[label] = slots
        print()

    # 交叉比对：派生接口的槽 3/4 应当与基接口一致（同一个实现）
    a = results.get("ITfTextInputProcessor")
    b = results.get("ITfTextInputProcessorEx")
    if a and b:
        print("--- 交叉比对 ---")
        print("    基接口槽 3/4 = %s / %s" % (hex(a[3]), hex(a[4])))
        print("    派接口槽 3/4 = %s / %s" % (hex(b[3]), hex(b[4])))
        print("    槽3 相同 = %s ; 槽4 相同 = %s" % (a[3] == b[3], a[4] == b[4]))
        print("    派接口槽 5 = %s" % hex(b[5]))
    print()
    print("=== dump 结束（未调用任何槽）===")
    return 0


def call_slot(slot):
    """在原生层调用 ITfTextInputProcessorEx 的第 slot 个槽。"""
    h, p = create(IID_ITfTextInputProcessorEx)
    print("=== 真调槽 %d ===" % slot)
    print("CoCreateInstance(ITfTextInputProcessorEx) hr=%s ptr=%s" % (hr(h), hex(p.value or 0)))
    if h != 0 or not p.value:
        print("[FAIL] 拿不到接口，无法调用")
        return 1

    slots = vtable_of(p)
    fn_addr = slots[slot]
    print("槽 %d 地址 = %s" % (slot, hex(fn_addr)))

    # HRESULT __stdcall fn(void* this, void* ptim, UINT tid, DWORD flags)
    proto = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p,
                               ctypes.c_uint, ctypes.c_uint)
    fn = proto(fn_addr)

    # Activate / ActivateEx 传 ptim=NULL -> 我们的托管实现应当返回 E_INVALIDARG(0x80070057)
    # Deactivate 是 slot 4，签名只有 this。
    if slot == 4:
        proto2 = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p)
        fn2 = proto2(fn_addr)
        print("即将调用 Deactivate() ...")
        sys.stdout.flush()
        r = fn2(ctypes.c_void_p(p.value))
    else:
        print("即将调用 slot %d (this, NULL, 0, 0) ..." % slot)
        sys.stdout.flush()
        r = fn(ctypes.c_void_p(p.value), None, 0, 0)

    print("返回 hr=%s" % hr(r))
    if r == 0x80070057:
        print("[ OK ] E_INVALIDARG —— 正是 TextService 里 `if (ptim == null) return E_INVALIDARG;` 分支")
        print("       说明托管方法体真的通过这个槽执行了")
    return 0


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "dump"
    if mode == "dump":
        sys.exit(dump())
    elif mode == "call":
        sys.exit(call_slot(int(sys.argv[2])))
    else:
        print("用法: probe_vtable.py dump | call <slot>")
        sys.exit(2)
