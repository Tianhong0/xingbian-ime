# -*- coding: utf-8 -*-
"""把地址映射到已加载模块（判断 0x7FFA192C12F0 之类地址是谁的代码）。"""
import ctypes
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

k32 = ctypes.WinDLL("kernel32")
psapi = ctypes.WinDLL("psapi")
k32.GetCurrentProcess.restype = ctypes.c_void_p
psapi.EnumProcessModulesEx.restype = ctypes.c_int
psapi.EnumProcessModulesEx.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint,
                                       ctypes.c_void_p, ctypes.c_uint]
psapi.GetModuleFileNameExW.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                       ctypes.c_wchar_p, ctypes.c_uint]
psapi.GetModuleInformation.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                       ctypes.c_uint]

PROC = ctypes.c_void_p(k32.GetCurrentProcess())

mods = []
buf = ctypes.create_string_buffer(8 * 512)
need = ctypes.c_uint(0)
if psapi.EnumProcessModulesEx(PROC, buf, len(buf), ctypes.byref(need), 3):
    n = need.value // 8
    handles = struct.unpack_from("<%dQ" % n, buf.raw, 0)
    for h in handles:
        MODINFO = ctypes.create_string_buffer(24)
        if not psapi.GetModuleInformation(PROC, ctypes.c_void_p(h), MODINFO, 24):
            continue
        # MODULEINFO { LPVOID lpBaseOfDll; DWORD SizeOfImage; LPVOID EntryPoint; }
        base, size = struct.unpack_from("<QI", MODINFO.raw, 0)
        nm = ctypes.create_string_buffer(520)
        psapi.GetModuleFileNameExW(PROC, ctypes.c_void_p(h), nm, 260)
        mods.append((base, size, nm.value))

mods.sort()
print("=== 已加载模块 (%d) ===" % len(mods))
for base, size, nm in mods:
    print("  0x%016X - 0x%016X  %s" % (base, base + size, nm))

TARGETS = []
for a in sys.argv[1:]:
    TARGETS.append(int(a, 16))

if TARGETS:
    print()
    print("=== 地址归属 ===")
    for a in TARGETS:
        hit = None
        for base, size, nm in mods:
            if base <= a < base + size:
                hit = (base, size, nm)
        if hit:
            print("  0x%016X -> %s  (+0x%X)" % (a, hit[2], a - hit[0]))
        else:
            print("  0x%016X -> (不在任何已加载模块内) —— 动态/未映射代码" % a)
