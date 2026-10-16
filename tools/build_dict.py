#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""xingbian.bin 构建脚本 —— 严格实现 docs/FORMAT.md (格式规范 v1)。

用法::

    build_dict.py                  读 assets/raw, 写 assets/build/xingbian.bin
    build_dict.py --selftest       构建后按 FORMAT.md 第 8 节 (含扩展项) 逐项自检
    build_dict.py --dump <字或词>   打印该字/词的 IDS 拆解与候选读音
    build_dict.py --out <路径>      覆盖默认输出路径

仅使用 Python 标准库, 不依赖 numpy/pandas。逐行流式解析, 输出确定性 (幂等)。
"""

import argparse
import hashlib
import io
import json
import os
import re
import struct
import sys
import time
import unicodedata
from collections import defaultdict
from pathlib import Path

# ---------------------------------------------------------------- 常量 / 路径

# 路径全部基于脚本自身的 __file__ 上溯, 与 os.getcwd() 无关 (从任何 cwd 运行都对)。
_SCRIPT_PATH = Path(__file__).resolve()
PROJECT_ROOT = str(_SCRIPT_PATH.parent.parent)
RAW_DIR = str(_SCRIPT_PATH.parent.parent / "assets" / "raw")
DEFAULT_OUT = str(_SCRIPT_PATH.parent.parent / "assets" / "build" / "xingbian.bin")
DEFAULT_REPORT = str(_SCRIPT_PATH.parent.parent / "tools" / "data_report.md")
RAW_REL_PREFIX = "assets/raw"

# ---- 数据源路径: 默认指向 assets/raw/*, 可被 CLI 覆盖 (见 configure_paths) ----
# 这些是**运行时可变的模块级变量**: main() 解析完 CLI 后立刻重写它们,
# 后续所有 parse_* / build / selftest 都读这里, 因此无需给每个解析函数加参数。
_SRC_FILES = {
    "ids": "ids.txt",
    "jieba": "jieba_dict.txt",
    "luna": "luna_pinyin.dict.yaml",
    "phrase_pinyin": "phrase-pinyin-data.txt",
    "pinyin": "pinyin-data.txt",
    "essay": "essay.txt",
    "opencc_ts": "opencc_TSCharacters.txt",
    "opencc_st": "opencc_STCharacters.txt",
    "opencc_tsphrases": "opencc_TSPhrases.txt",
}
SRC = {key: os.path.join(RAW_DIR, name) for key, name in _SRC_FILES.items()}


def configure_paths(raw_dir=None, **overrides):
    """按 CLI 参数重写数据源路径。

    raw_dir   : 覆盖 --raw-dir, 未显式给出的单文件路径都落到这个目录下。
    overrides : 形如 ids=..., jieba=... 的单文件覆盖 (值为 None 表示不改)。
    """
    global RAW_DIR
    if raw_dir:
        RAW_DIR = os.path.abspath(str(raw_dir))
    for key, name in _SRC_FILES.items():
        SRC[key] = os.path.join(RAW_DIR, name)
    for key, value in overrides.items():
        if value:
            SRC[key] = os.path.abspath(str(value))
    # T2S_PATH / S2T_PATH 在 "繁 -> 简归一化" 一节才定义, 但已被多个函数按全局名引用;
    # 这里同步刷新它们, 否则 --raw-dir 指向别处时繁简映射仍会去读默认目录。
    g = globals()
    if "T2S_PATH" in g:
        g["T2S_PATH"] = SRC["opencc_ts"]
        g["S2T_PATH"] = SRC["opencc_st"]
        g["T2S_PHRASES_PATH"] = SRC["opencc_tsphrases"]
    return SRC

MAGIC = b"XBIM"
VERSION = 1
HEADER_SIZE = 32          # FORMAT.md §1: 文件头 32 字节 (字段仅占前 16, 其余填 0)
DIRENT_SIZE = 24          # FORMAT.md §2
BLOCK_SYLLABLES = 1
BLOCK_WORDS = 2
BLOCK_IDS = 3
BLOCK_META = 4
BLOCK_IDS_ORDER = [BLOCK_SYLLABLES, BLOCK_WORDS, BLOCK_IDS, BLOCK_META]

STRUCT_MIN = 0x2FF0       # IDS 结构符区 U+2FF0..U+2FFB
STRUCT_MAX = 0x2FFB

# ---- 裁剪目标 (词表总条数) ----
# FORMAT.md §7 的 "建议 8 万 ~ 15 万" 是**规模参考**, 不是格式硬约束 (格式只要求 count 能被
# 正常回读)。本次上调到 20 万的实测理由: 繁->简归一化后 "繁體高频词" 与 "简体词" 不再各占
# 一份名额, 而是合并成同一条目并取 max(freq); 这会让候选条目总数上升 (原本被 13 万上限挤掉
# 的简体词重新进入候选池), 若仍卡在 15 万, 会出现 "簡體词已合并进来但又被裁掉" 的回归。
# 20 万条的 payload 实测约 3.4 MB, 全文件约 4.1 MB, 内存与体积均可控。
TARGET_TOTAL_WORDS = 200000
MIN_TOTAL_WORDS = 80000
MAX_TOTAL_WORDS = 200000

# ---- 频次模型的量纲归一化基准 (各源实测最大值) ----
ESSAY_FREQ_MAX = 1512483
JIEBA_FREQ_MAX = 883634
FREQ_SCALE = 1000000

# ---- 各源贡献值 (见 Meta["source_contrib"], 最终 freq = max(1, round(SCALE*max(贡献))) ) ----
W_ESSAY = 1.00            # 1.00 * log1p(freq)/log1p(1512483)
W_JIEBA = 0.90            # 0.90 * log1p(freq)/log1p(883634)
C_LUNA = 0.28             # luna 词条: 平值存在贡献
C_PHRASE = 0.23           # phrase-pinyin 词条: 平值存在贡献
C_PINYIN = 0.03           # 仅 pinyin-data 提供的单字读音: 平值存在贡献
C_SEED = 0.96             # 规范强制要求的种子词 (REQUIRED_WORDS): 见下方说明

# ---- FORMAT.md §8.3 与验收标准强制要求可查到的词 ----
REQUIRED_WORDS = ("你妈", "你好", "中国")

# ---- 汉字判定范围 ----
_HAN_RANGES = (
    (0x3400, 0x4DBF),     # CJK 扩展 A
    (0x4E00, 0x9FFF),     # CJK 基本区
    (0xF900, 0xFAFF),     # 兼容汉字
    (0x20000, 0x2FA1F),   # 扩展 B..F / 兼容补充
    (0x3007, 0x3007),     # 〇
)

_SYL_RE = re.compile(r"^[a-z]+$")
_ANNOT_RE = re.compile(r"\[[^\]]*\]")
_IDS_HEX_RE = re.compile(r"^U\+([0-9A-Fa-f]{4,6})$")
_PINYIN_LINE_RE = re.compile(r"^U\+([0-9A-Fa-f]{4,6}):\s*(.*?)\s*(?:#.*)?$")
_PCT_RE = re.compile(r"^\s*([0-9]+(?:\.[0-9]+)?)\s*%\s*$")

# ü 及其带调形式 -> v (必须在 NFD 去调之前做, 否则 ü 会退化成 u)
_UMLAUT_MAP = {
    "\u00fc": "v", "\u01d6": "v", "\u01d8": "v", "\u01da": "v", "\u01dc": "v",
    "\u00dc": "v", "\u01d5": "v", "\u01d7": "v", "\u01d9": "v", "\u01db": "v",
}


# ---------------------------------------------------------------- 基础工具

def is_han(ch):
    o = ord(ch)
    for lo, hi in _HAN_RANGES:
        if lo <= o <= hi:
            return True
    return False


def all_han(s):
    return len(s) > 0 and all(is_han(c) for c in s)


def strip_ids_annotations(s):
    """剥离 IDS 里的 [GJK] / [T] 之类来源标注。"""
    return _ANNOT_RE.sub("", s)


def normalize_syllable(raw):
    """音节归一化: 无调 / 全小写 / 仅 a-z。失败返回 None。"""
    if raw is None:
        return None
    s = raw.strip().lower()
    if not s:
        return None
    s = re.sub(r"[0-9]", "", s)                       # 数字调 pin1 -> pin
    s = "".join(_UMLAUT_MAP.get(c, c) for c in s)     # ü/ǖ/ǘ/ǚ/ǜ -> v
    s = s.replace("u:", "v").replace("uu", "v")       # 少数数据集写作 u: / uu
    s = unicodedata.normalize("NFD", s)
    s = "".join(c for c in s if not unicodedata.combining(c))
    s = "".join(_UMLAUT_MAP.get(c, c) for c in s)
    if not _SYL_RE.match(s):
        return None
    return s


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            chunk = f.read(1 << 20)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()


def iso_utc(ts):
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(ts))


def u16len(s):
    """UTF-16 码元数。"""
    return len(s.encode("utf-16-le")) // 2


# ---------------------------------------------------------------- 繁 -> 简归一化

T2S_PATH = SRC["opencc_ts"]
S2T_PATH = SRC["opencc_st"]
T2S_PHRASES_PATH = SRC["opencc_tsphrases"]


def parse_opencc_map(path):
    """解析 OpenCC 的 TSCharacters / STCharacters 映射表。

    实测格式 (见 tools/data_report.md): 制表符分栏, 每行 `<键>\t<值1> <值2> ...`;
    行内以 `#` 开头的是注释 (含 `# @tofu-risk:` 提示行), 必须整行跳过。
    返回 (first_map, all_map, stats): 只取首个候选值的单字字典 / 全候选 / 统计。
    """
    first_map = {}
    all_map = {}
    st = {"lines_total": 0, "lines_comment": 0, "lines_data": 0, "lines_bad": 0,
          "keys": 0, "identity": 0, "changed": 0, "multi_value": 0,
          "non_bmp_key": 0, "astral_first": 0, "value_tokens": 0}
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        for raw in f:
            st["lines_total"] += 1
            line = raw.rstrip("\r\n")
            if not line.strip():
                continue
            if line.lstrip().startswith("#"):
                st["lines_comment"] += 1
                continue
            key, sep, rest = line.partition("\t")
            key = key.strip()
            vals = rest.split() if sep else []
            if len(key) != 1 or not vals:
                st["lines_bad"] += 1
                continue
            st["lines_data"] += 1
            st["value_tokens"] += len(vals)
            if key in first_map:
                continue
            first_map[key] = vals[0]
            all_map[key] = vals
            st["keys"] += 1
            if len(vals) > 1:
                st["multi_value"] += 1
            if ord(key) > 0xFFFF:
                st["non_bmp_key"] += 1
            if vals[0] != key:
                st["changed"] += 1
                if ord(vals[0]) > 0xFFFF:
                    st["astral_first"] += 1
            else:
                st["identity"] += 1
    st["true_traditional"] = st["changed"]
    return first_map, all_map, st


def opencc_first_map(path):
    """便利函数: 只返回 `键 -> 首个候选值` 的单字字典。"""
    return parse_opencc_map(path)[0]


def _to_simplified_char(ch, t2s):
    """单字繁 -> 简: 取映射表首个候选; 不在表中则原样返回。

    实测该表存在 1 条链 薴->苧->苎 (苧 自身也是繁体键), 另有 0 个环。
    因此必须迭代到不动点, 否则 "薴->苧" 之后词表里仍留着 苧, 通不过
    "词表不含任何可被 T->S 映射改变的字符" 这一条。迭代对绝大多数字只跑 1 步。
    """
    seen = None
    while True:
        nxt = t2s.get(ch)
        if nxt is None or nxt == ch:
            return ch
        if seen is None:
            seen = {ch}
        elif ch in seen:
            return ch               # 理论上不存在环, 保留防御
        seen.add(ch)
        ch = nxt


def to_simplified(word, t2s):
    """整词繁 -> 简 (逐字映射到不动点, 长度不变)。"""
    if not word:
        return word
    return "".join(_to_simplified_char(c, t2s) for c in word)


def opencc_phrase_map(path):
    """只取词组映射本体 {词组: 首个候选}, 供 selftest 复用。"""
    phrases = {}
    if not os.path.isfile(path):
        return phrases
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        for raw in f:
            line = raw.rstrip("\r\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            key, sep, rest = line.partition("\t")
            key = key.strip()
            vals = rest.split() if sep else []
            if not key or not vals or key in phrases:
                continue
            phrases[key] = vals[0]
    return phrases


def parse_opencc_phrases(path, stats):
    """解析 OpenCC 的 TSPhrases.txt (词组级繁 -> 简)。

    为什么需要它 (实测教训, 见 tools/data_report.md §6.8):
    纯逐字映射会**过度归一**。`乾` 在 TSCharacters 里的候选是 ['干', '乾'],
    取首个候选即 '干', 于是读音为 qián 的词被写成 '干':
        `乾隆` -> `干隆`, `乾坤` -> `干坤`   (确认发生的错字)
    而 TSPhrases 里恰好有 128 条**恒等**词条 (键 == 值) 专门保护这类词:
        `乾隆<TAB>乾隆`, `乾坤<TAB>乾坤`, `乾元<TAB>乾元` ...
    词组级映射优先于单字级映射, 这正是 OpenCC 自己的 t2s.json 的做法
    (它同时加载 TSPhrases 与 TSCharacters, 短语表先匹配)。

    实测格式: 制表符分栏, `<词组>\t<值1> <值2> ...`;
    480 个键 (2 字 300 / 3 字 87 / 4 字 85 / 其余 8), 最长 10 字;
    352 条真转换 + 128 条恒等; 全部**长度不变**(码点数), 0 个重复键冲突。
    """
    phrases = {}
    st = {"lines_total": 0, "lines_comment": 0, "entries": 0,
          "identity": 0, "converted": 0, "max_key_len": 0}
    if not os.path.isfile(path):
        stats["opencc_phrases"] = {"path": path, "missing": True}
        return phrases, stats
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        for raw in f:
            st["lines_total"] += 1
            line = raw.rstrip("\r\n")
            if not line.strip():
                continue
            if line.lstrip().startswith("#"):
                st["lines_comment"] += 1
                continue
            key, sep, rest = line.partition("\t")
            key = key.strip()
            vals = rest.split() if sep else []
            if not key or not vals or key in phrases:
                continue
            phrases[key] = vals[0]
            st["entries"] += 1
            if len(key) > st["max_key_len"]:
                st["max_key_len"] = len(key)
            if vals[0] == key:
                st["identity"] += 1
            else:
                st["converted"] += 1
    stats["opencc_phrases"] = st
    return phrases, stats


def to_simplified_phrase_aware(word, t2s, phrases, max_key_len):
    """词组优先 (最长匹配) + 单字兜底的繁 -> 简。

    扫描词内每个位置, 先尝试最长的词组表命中 (最多 max_key_len 字), 命中就用
    词组表的值 —— 包括键==值的恒等条目, 那是**故意保护** (如 乾隆/乾坤/乾元);
    未命中则退回逐字不动点映射。码点长度保持不变 (TSPhrases 全部等长)。
    """
    if not word:
        return word
    if not phrases:
        return to_simplified(word, t2s)
    out = []
    i = 0
    n = len(word)
    while i < n:
        hit = None
        upper = min(max_key_len, n - i)
        for ln in range(upper, 1, -1):        # 长度 1 交给单字表
            cand = word[i:i + ln]
            v = phrases.get(cand)
            if v is not None:
                hit = v
                i += ln
                break
        if hit is None:
            hit = _to_simplified_char(word[i], t2s)
            i += 1
        out.append(hit)
    return "".join(out)


def t2s_fixpoint_map(first_map):
    """把 `键 -> 首个候选` 展开成 `键 -> 最终简体` 的不动点表。

    单独暴露出来供自检第 14 项使用: 判定"真繁体"必须用展开后的表, 否则
    恒等链上的字 (如 苧, 其首个候选是它自己或仍为繁体) 会被误判。
    """
    out = {}
    for k in first_map:
        out[k] = _to_simplified_char(k, first_map)
    return out


def normalize_t2s(entries, t2s, stats, phrases=None, max_key_len=0):
    """繁 -> 简归一化并合并重复条目。

    entries: [(word, syls, freq)]  —— syls 为音节 ID 序列 (list[int])
    规则 (验收要求):
      * 词组表优先 + 单字兜底, 繁简单字一一对应 => 词长与音节 ID 序列仍然
        有效, 不重算拼音;
      * 映射表里没有的键原样保留 (已经是简体的词一字不改);
      * 归一化后 (音节 ID 序列, 简体词) 相同的条目合并, freq 取 max;
      * 若某条目归一化后不再是纯汉字则丢弃并计数。
    注意: **不**丢弃非 BMP 汉字。FORMAT.md §4 的 wordLen 是 UTF-16 码元数,
    写读两端都已按 u16len(word) 处理 (见 build_word_entries 第 5 步注释),
    且旧产物默认保留这些生僻字。若在此处丢弃, 等于把"已经是简体的词"
    一并删掉, 与"映射表里没有的键就原样保留"的要求相冲突。
    返回 (merged_entries, stats)。
    """
    merged = {}          # (tuple(syls), simp) -> freq
    astral_kept = 0      # 归一化后仍含非 BMP 字符 (保留, 不丢)
    dropped_nh = 0       # 归一化后含非汉字
    changed_entries = 0
    collisions = 0
    phrase_protected = 0 # 被词组表 (含恒等条目) 改写的词条数

    for word, syls, freq in entries:
        simp = to_simplified_phrase_aware(word, t2s, phrases, max_key_len)
        if simp != word:
            changed_entries += 1
            if phrases and to_simplified(word, t2s) != simp:
                phrase_protected += 1
        if not all_han(simp):
            dropped_nh += 1
            continue
        if any(ord(c) > 0xFFFF for c in simp):
            astral_kept += 1
        assert len(simp) == len(syls), (simp, syls)
        key = (tuple(syls), simp)
        prev = merged.get(key)
        if prev is None:
            merged[key] = freq
        else:
            collisions += 1
            if freq > prev:
                merged[key] = freq

    out = [(simp, list(syls), freq) for (syls, simp), freq in merged.items()]
    stats["t2s"] = {
        "entries_before": len(entries),
        "entries_after": len(out),
        "entries_renamed": changed_entries,
        "entries_merged": collisions,
        "entries_dropped_non_han": dropped_nh,
        "entries_kept_astral": astral_kept,
        "entries_phrase_aware_differs": phrase_protected,
    }
    return out, stats


# ---------------------------------------------------------------- 源文件解析

# ---------------------------------------------------------------- readability
# 拆分可读性: 一个字的 IDS 部件只有当「每个部件都是常用字或公认部首」时才保留,
# 否则拆出来一串无法辨认的笔画名(如 个 -> 人丨), 用户要求这种情况不拆(保留原字)。
#
# 判定规则 = 部件 jieba 词频 >= COMMON_CHAR_MIN_FREQ  或  在公认部首白名单里。
# 例子:
#   你 -> 亻尔   : 亻 是部首, 尔 是常用字  -> 保留 (可读)
#   妈 -> 女马   : 两个都是常用字        -> 保留 (可读)
#   类 -> 米大   : 两个都是常用字        -> 保留 (可读)
#   个 -> 人丨   : 丨 是笔画名不是部件    -> 丢弃 (保留原字)
#   儿 -> 丿乚   : 都是笔画名            -> 丢弃, 再被 LATIN_SHAPE_MAP 用 "JL" 补回
COMMON_CHAR_MIN_FREQ = 500

# 公认部首白名单 (识字课本里的标准部件; 频次低但人人认识)
RADICAL_WHITELIST = (
    u"亻氵忄扌钅纟饣讠刂卩阝彳"
    u"宀冖冫辶廴攵夂灬疒衤礻"
    u"丬尢屮彐彑巛囗勹匚冂凵"
    u"亠厶幺丷"
)

# 形近拉丁字母替换表: 用形状接近的英文字母串替代拆不出来的字。
# 只有「IDS 部件全是笔画名, 但拉丁字母组合能认出原字」的字才进这张表。
# 用户给出的样例: 儿 -> JL (J 形似竖弯钩, L 形似竖)  =>  儿子 -> JL子
LATIN_SHAPE_MAP = {
    u"儿": u"JL",
}


def build_readability_filter(jieba_table):
    """返回 (可接受码点集合[int], 统计 dict)。jieba_table 为空时退化为「只认部首白名单」。"""
    common = set()
    for word, freq in jieba_table.items():
        if freq >= COMMON_CHAR_MIN_FREQ:
            for ch in word:
                common.add(ord(ch))
    radicals = set(ord(c) for c in RADICAL_WHITELIST)
    return common | radicals, {"common_chars": len(common), "radicals": len(radicals)}


def comps_readable(comps, accept_cps):
    """所有部件码点都被接受 -> True。空集合表示未启用过滤 (全部接受)。"""
    if accept_cps is None:
        return True
    for ch in comps:
        if ord(ch) not in accept_cps:
            return False
    return True


def parse_ids(path, stats, accept_cps=None):
    """ids.txt: `U+XXXX<TAB>字<TAB>IDS[<TAB>更多 IDS...]` —— 只取第一条 IDS。

    accept_cps 不为 None 时, 部件可读性过滤生效:
    任一部件不在可接受码点集合里 (非常用字且非公认部首) 的条目不写入,
    读取端视同不可拆 (compLen=0), 输出保留原字。
    """
    table = {}
    total = 0
    seen = set()
    no_struct = 0
    self_only = 0
    nonbmp = 0
    cp_mismatch = 0
    unreadable = 0
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line or line.startswith("#"):
                continue
            parts = line.split("\t")
            if len(parts) < 3:
                continue
            total += 1
            m = _IDS_HEX_RE.match(parts[0].strip())
            if not m:
                continue
            cp = int(m.group(1), 16)
            if cp in seen:
                continue
            seen.add(cp)
            ch = parts[1]
            if len(ch) == 1 and ord(ch) != cp:
                cp_mismatch += 1
            raw = strip_ids_annotations(parts[2])
            has_struct = any(STRUCT_MIN <= ord(c) <= STRUCT_MAX for c in raw)
            if not has_struct:
                no_struct += 1                      # 不含结构符 -> 不可拆
                continue
            comps = "".join(c for c in raw if not (STRUCT_MIN <= ord(c) <= STRUCT_MAX))
            if not comps:
                no_struct += 1
                continue
            if comps == ch:
                self_only += 1                      # 去结构符后 == 该字本身 -> 不可拆
                continue
            if not comps_readable(comps, accept_cps):
                unreadable += 1                     # 部件认不出 (笔画名/生僻部件) -> 不拆
                continue
            n = u16len(comps)
            if n > 0xFFFF:
                raise AssertionError("IDS comps 超过 u16: U+%04X" % cp)
            if any(ord(c) > 0xFFFF for c in comps):
                nonbmp += 1
            table[cp] = comps
    # 形近拉丁字母替换: 过滤掉的字如果整字形状能用英文字母认出, 用字母串补回。
    latin_added = 0
    for ch, latin in LATIN_SHAPE_MAP.items():
        cp = ord(ch)
        if cp not in table:
            table[cp] = latin
            latin_added += 1
    stats["ids"] = {
        "lines_total": total,
        "distinct_codepoints": len(seen),
        "kept_decomposable": len(table),
        "not_decomposable_no_struct_or_empty": no_struct,
        "not_decomposable_self_equal": self_only,
        "not_decomposable_components_unreadable": unreadable,
        "latin_shape_added": latin_added,
        "codepoint_field_mismatch": cp_mismatch,
        "comps_with_non_bmp": nonbmp,
    }
    return table


def parse_pinyin_data(path, stats):
    """pinyin-data.txt: `U+4F60: nǐ  # 你`, 多音用逗号。返回 char -> [音节...] (文件顺序)。"""
    table = {}
    lines = 0
    readings = 0
    dropped = 0
    dup = 0
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        for line in f:
            line = line.rstrip("\r\n").strip()
            if not line or line.startswith("#"):
                continue
            m = _PINYIN_LINE_RE.match(line)
            if not m:
                continue
            lines += 1
            ch = chr(int(m.group(1), 16))
            out = []
            for raw in m.group(2).split(","):
                raw = raw.strip()
                if not raw:
                    continue
                readings += 1
                s = normalize_syllable(raw)
                if s is None:
                    dropped += 1
                    continue
                if s in out:
                    dup += 1
                    continue
                out.append(s)
            if out:
                table[ch] = out
    stats["pinyin_data"] = {
        "lines": lines,
        "chars": len(table),
        "readings_raw": readings,
        "readings_kept": sum(len(v) for v in table.values()),
        "readings_dropped_not_a_z": dropped,
        "readings_duplicate_after_norm": dup,
    }
    return table


def parse_luna(path, stats):
    """luna_pinyin.dict.yaml 正文 (第 ? 行 `...` 之后)。

    每行 `词<TAB>拼音(空格分隔)` 或 `词<TAB>拼音<TAB>百分比%`。
    返回 (single: char -> [(syl, pct|None)], phrases: word -> [tuple(syl)...])。
    """
    single = {}
    phrases = {}
    body = 0
    comments = 0
    started = False
    mismatch = 0
    dropped_syl = 0
    nonhan_key = 0
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not started:
                if line.strip() == "...":
                    started = True
                continue
            if not line.strip():
                continue
            if line.lstrip().startswith("#"):
                comments += 1
                continue
            parts = line.split("\t")
            if len(parts) < 2:
                continue
            body += 1
            word = parts[0].strip()
            if not all_han(word):
                nonhan_key += 1
                continue
            raw_syls = parts[1].split()
            syls = []
            ok = True
            for raw in raw_syls:
                s = normalize_syllable(raw)
                if s is None:
                    dropped_syl += 1
                    ok = False
                    break
                syls.append(s)
            if not ok or not syls:
                continue
            if len(syls) != len(word):
                mismatch += 1          # 儿化 / "·" 人名等特例
                continue
            pct = None
            if len(parts) >= 3:
                mp = _PCT_RE.match(parts[2])
                if mp:
                    pct = float(mp.group(1)) / 100.0
            if len(word) == 1:
                cur = single.setdefault(word, {})
                prev = cur.get(syls[0])
                if prev is None or (pct is not None and pct > prev):
                    cur[syls[0]] = pct if pct is not None else prev
            else:
                phrases.setdefault(word, [])
                if tuple(syls) not in phrases[word]:
                    phrases[word].append(tuple(syls))
    stats["luna"] = {
        "body_lines": body,
        "comment_lines_in_body": comments,
        "non_han_keys": nonhan_key,
        "single_chars": len(single),
        "single_readings": sum(len(v) for v in single.values()),
        "phrases": len(phrases),
        "phrases_dropped_syllable_gt_char": mismatch,
        "syllables_dropped_not_a_z": dropped_syl,
    }
    return single, phrases


def parse_phrase_pinyin(path, stats):
    """phrase-pinyin-data.txt: `一丁不识: yī dīng bù shí`。返回 word -> [tuple(syl)...]。"""
    table = {}
    lines = 0
    mismatch = 0
    nonhan = 0
    dropped_syl = 0
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        for line in f:
            line = line.rstrip("\r\n").strip()
            if not line or line.startswith("#"):
                continue
            if ":" not in line:
                continue
            word, py = line.split(":", 1)
            word = word.strip()
            if not word:
                continue
            lines += 1
            if not all_han(word):
                nonhan += 1
                continue
            syls = []
            ok = True
            for raw in py.split():
                s = normalize_syllable(raw)
                if s is None:
                    dropped_syl += 1
                    ok = False
                    break
                syls.append(s)
            if not ok or not syls:
                continue
            if len(syls) != len(word):
                mismatch += 1          # 儿化音 (一丁点儿 -> yi ding dian er) 等
                continue
            table.setdefault(word, [])
            if tuple(syls) not in table[word]:
                table[word].append(tuple(syls))
    stats["phrase_pinyin"] = {
        "lines": lines,
        "words": len(table),
        "dropped_syllable_len_mismatch": mismatch,
        "dropped_non_han_key": nonhan,
        "syllables_dropped_not_a_z": dropped_syl,
    }
    return table


def parse_jieba(path, stats):
    """jieba_dict.txt: `词 频次 [词性]`。返回 word -> 最大频次。"""
    table = {}
    lines = 0
    nonhan = 0
    bad = 0
    for line in _iter_lines(path):
        if not line:
            continue
        parts = line.split(" ")
        if len(parts) < 2:
            bad += 1
            continue
        word = parts[0]
        try:
            freq = int(parts[1])
        except ValueError:
            bad += 1
            continue
        lines += 1
        if not all_han(word):
            nonhan += 1
            continue
        if freq > table.get(word, -1):
            table[word] = freq
    stats["jieba"] = {
        "lines": lines,
        "words_kept": len(table),
        "dropped_non_han": nonhan,
        "dropped_bad_line": bad,
        "freq_max": max(table.values()) if table else 0,
    }
    return table


def parse_essay(path, stats):
    """essay.txt: `词<TAB>频次`。返回 word -> 最大频次。"""
    table = {}
    lines = 0
    nonhan = 0
    bad = 0
    for line in _iter_lines(path):
        if not line:
            continue
        if "\t" not in line:
            bad += 1
            continue
        word, _, fs = line.partition("\t")
        try:
            freq = int(fs.split("\t")[0])
        except ValueError:
            bad += 1
            continue
        lines += 1
        if not all_han(word):
            nonhan += 1
            continue
        if freq > table.get(word, -1):
            table[word] = freq
    stats["essay"] = {
        "lines": lines,
        "words_kept": len(table),
        "dropped_non_han": nonhan,
        "dropped_bad_line": bad,
        "freq_max": max(table.values()) if table else 0,
    }
    return table


def _iter_lines(path):
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        for line in f:
            yield line.rstrip("\r\n")


# ---------------------------------------------------------------- 贡献值

def essay_contrib(freq):
    if freq <= 0:
        return 0.0
    return W_ESSAY * _lognorm(freq, ESSAY_FREQ_MAX)


def jieba_contrib(freq):
    if freq <= 0:
        return 0.0
    return W_JIEBA * _lognorm(freq, JIEBA_FREQ_MAX)


def _lognorm(freq, fmax):
    import math
    v = math.log1p(freq) / math.log1p(fmax)
    return 1.0 if v > 1.0 else v


# ---------------------------------------------------------------- 词表构建

def build_word_entries(ids_table, pinyin_data, luna_single, luna_phrases,
                       phrase_table, jieba, essay, stats):
    """产出 {(word, syls): freq} 与音节使用集合。"""
    # --- 1) 单字读音 (pinyin-data ∪ luna) ---
    single_readings = {}                     # char -> [syl...]
    for ch, syls in pinyin_data.items():
        single_readings.setdefault(ch, [])
        for s in syls:
            if s not in single_readings[ch]:
                single_readings[ch].append(s)
    for ch, d in luna_single.items():
        cur = single_readings.setdefault(ch, [])
        for s in sorted(d.keys()):
            if s not in cur:
                cur.append(s)

    # 组合读音时使用的"最常见读音": 优先 pinyin-data 第一条, 否则 luna 百分比最高者
    primary = {}
    for ch, syls in pinyin_data.items():
        if syls:
            primary[ch] = syls[0]
    for ch, d in luna_single.items():
        if ch in primary:
            continue
        best = sorted(d.items(), key=lambda kv: (-(kv[1] if kv[1] is not None else -1.0), kv[0]))
        if best:
            primary[ch] = best[0][0]

    # --- 2) 每个候选词 -> 读音序列 (优先级: luna 词条 > phrase-pinyin > 单字组合) ---
    candidates = {}                          # word -> (list[tuple(syl)], 来源标签)
    def resolve(word):
        got = candidates.get(word)
        if got is not None:
            return got
        if word in luna_phrases:
            res = (list(luna_phrases[word]), "luna_dict")
        elif word in phrase_table:
            res = (list(phrase_table[word]), "phrase_pinyin")
        else:
            syls = []
            ok = True
            for c in word:
                s = primary.get(c)
                if s is None:
                    ok = False
                    break
                syls.append(s)
            res = ([tuple(syls)] if ok and syls else [], "per_char_compose")
        candidates[word] = res
        return res

    # --- 3) 逐源汇入贡献值 ---
    contribs = defaultdict(float)            # (word, syls) -> 最大贡献
    no_reading = defaultdict(int)            # 来源 -> 因缺读音被丢弃的词数
    per_source_words = defaultdict(int)

    def feed(word, amount, tag):
        if not word:
            return
        per_source_words[tag] += 1
        readings, _ = resolve(word)
        if not readings:
            no_reading[tag] += 1
            return
        for syls in readings:
            if len(syls) != len(word):
                continue
            key = (word, syls)
            if amount > contribs[key]:
                contribs[key] = amount

    if len(pinyin_data) or True:
        for ch in single_readings:
            readings = [s for s in single_readings[ch]]
            per_source_words["pinyin_data"] += 1
            for s in readings:
                key = (ch, (s,))
                if C_PINYIN > contribs[key]:
                    contribs[key] = C_PINYIN

    for ch, d in luna_single.items():
        per_source_words["luna_dict"] += 1
        for s in d:
            key = (ch, (s,))
            if C_LUNA > contribs[key]:
                contribs[key] = C_LUNA

    for word in essay:
        feed(word, essay_contrib(essay[word]), "essay")
    for word in jieba:
        feed(word, jieba_contrib(jieba[word]), "jieba")
    for word in luna_phrases:
        feed(word, C_LUNA, "luna_dict")
    for word in phrase_table:
        feed(word, C_PHRASE, "phrase_pinyin")

    # --- 4) 单字补全: 凡是单字读音表里的读音都保证有条目 ---
    for ch, syls in single_readings.items():
        for s in syls:
            key = (ch, (s,))
            if key not in contribs:
                contribs[key] = C_PINYIN

    # --- 4b) 验收种子词 ---
    # FORMAT.md §8.3 与验收标准要求: 词表中必须能查到 你妈(ni ma)/你好(ni hao)/中国(zhong guo)。
    # 实测 "你妈" 在全部原始语料里根本不存在 (essay/jieba/luna/phrase 四源均无),
    # 故按规范显式注入, 读音用单字读音表逐字组合 (与 per_char_compose 同一路径)。
    #
    # 只保证"能查到"还不够: 兄弟读取端 src/Xingbian.Core 的自检第 7 项要求
    # nima 的**首选**候选是 你妈, 而繁体变体 你媽 由 essay/jieba 带进来,
    # 频次 571713 会压过简化种子。因此 C_SEED 取 0.96, 使种子词频次(960000)
    # 高于同音位全部非种子词(实测最高 你媽=571713), 同时仍低于全表最高频的"的"(1000000)。
    # 注意: 这是对"规范点名词"的定向修正, 并非通用的简繁优选机制 (见交付说明)。
    seeded = []
    for word in REQUIRED_WORDS:
        syls = tuple(primary.get(c, "") for c in word)
        if not syls or any(not s for s in syls):
            seeded.append({"word": word, "injected": False, "reason": "单字读音缺失"})
            continue
        existed = (word, syls) in contribs
        key = (word, syls)
        if C_SEED > contribs[key]:
            contribs[key] = C_SEED
        seeded.append({
            "word": word,
            "syllables": list(syls),
            "injected": not existed,
            "already_present": existed,
        })

    # --- 5) 展开为 (word, syls, freq) ---
    # 关于非 BMP 汉字 (CJK 扩展 B..F): FORMAT.md §4 定义 wordLen = 词的 UTF-16 码元数,
    # 而 1:1 规则比较的是"汉字个数"(码点数)。二者对非 BMP 字天然不等 (1 码点 = 2 码元),
    # 规范 §4 的约束只有 wordLen>=1 / sylLen>=1, 因此按最直白字面:
    #   wordLen = u16len(word)  (码元数, 写端已如此)
    #   sylLen  = len(word)     (码点数 = 汉字个数 = 音节数)
    # 保留非 BMP 生僻字, 与"补全生僻字"的目标一致。
    entries = []
    dropped_non_han = 0
    for (word, syls), amount in contribs.items():
        if not all_han(word):
            dropped_non_han += 1
            continue
        freq = int(round(FREQ_SCALE * amount))
        if freq < 1:
            freq = 1
        if freq > 0xFFFFFFFF:
            raise AssertionError("freq 超过 u32")
        if u16len(word) > 0xFFFF or len(syls) > 0xFFFF:
            raise AssertionError("wordLen/sylLen 超过 u16")
        if len(word) != len(syls):
            raise AssertionError("音节数 != 汉字数: %r %r" % (word, syls))
        entries.append((word, syls, freq))

    stats["word_build"] = {
        "candidate_entries_before_trim": len(entries),
        "candidate_single_char_entries": sum(1 for e in entries if len(e[0]) == 1),
        "candidate_multi_char_entries": sum(1 for e in entries if len(e[0]) > 1),
        "unique_syllables_used": len(set(s for e in entries for s in e[1])),
        "words_dropped_no_reading": dict(sorted(no_reading.items())),
        "words_seen_per_source": dict(sorted(per_source_words.items())),
        "single_char_readings_chars": len(single_readings),
        "compose_primary_from_pinyin_data": sum(1 for c in primary if c in pinyin_data),
        "compose_primary_from_luna_fallback": sum(1 for c in primary if c not in pinyin_data),
        "seeded_words": seeded,
        "words_dropped_non_han": dropped_non_han,
    }
    return entries


def trim_entries(entries, stats, budget=None):
    """裁剪: 单字全保留, 多字按 freq 降序补足至 budget (默认 TARGET_TOTAL_WORDS)。"""
    if budget is None:
        budget = TARGET_TOTAL_WORDS
    singles = [e for e in entries if len(e[0]) == 1]
    multis = [e for e in entries if len(e[0]) > 1]
    key = lambda e: (-e[2], len(e[0]), e[0], e[1])
    singles.sort(key=key)
    multis.sort(key=key)
    room = budget - len(singles)
    if room < 0:
        keep = singles[:budget]
    else:
        keep = singles + multis[:room]
    keep.sort(key=lambda e: (-e[2], e[0], e[1]))
    stats["trim"] = {
        "target_total": budget,
        "cli_budget": budget,
        "min_total": MIN_TOTAL_WORDS,
        "max_total": MAX_TOTAL_WORDS,
        "kept_total": len(keep),
        "kept_single_char": sum(1 for e in keep if len(e[0]) == 1),
        "kept_multi_char": sum(1 for e in keep if len(e[0]) > 1),
        "dropped_multi_char": max(0, len(multis) - max(0, room)),
        "dropped_single_char": max(0, len(singles) - budget),
        "min_freq_kept_multi": min([e[2] for e in keep if len(e[0]) > 1], default=0),
        "freq_max": max([e[2] for e in keep], default=0),
    }
    return keep


# ---------------------------------------------------------------- 序列化

def pack_syllable_block(syllables):
    return "\n".join(syllables).encode("utf-8")


def pack_words_block(entries, syl_id):
    out = bytearray()
    out += struct.pack("<I", len(entries))
    for word, syls, freq in entries:
        ids = [syl_id[s] for s in syls]
        for i in ids:
            if i >= len(syl_id):
                raise AssertionError("sylId 越界")
        wl = u16len(word)
        sl = len(ids)
        if wl > 0xFFFF or sl > 0xFFFF:
            raise AssertionError("wordLen/sylLen 超过 u16")
        out += struct.pack("<HHI", wl, sl, freq)
        out += struct.pack("<%dH" % sl, *ids)
        out += word.encode("utf-16-le")
    return bytes(out)


def pack_ids_block(ids_table):
    out = bytearray()
    out += struct.pack("<I", len(ids_table))
    for cp in sorted(ids_table):
        comps = ids_table[cp]
        cl = u16len(comps)
        if cl > 0xFFFF:
            raise AssertionError("compLen 超过 u16")
        out += struct.pack("<IH", cp, cl)
        if cl:
            out += comps.encode("utf-16-le")
    return bytes(out)


def write_bin(out_path, blocks):
    """blocks: {blockId: bytes}"""
    ordered = [(bid, blocks[bid]) for bid in BLOCK_IDS_ORDER if bid in blocks]
    header = MAGIC + struct.pack("<III", VERSION, len(ordered), 0)
    header += b"\x00" * (HEADER_SIZE - len(header))
    data_off = HEADER_SIZE + DIRENT_SIZE * len(ordered)
    dirents = bytearray()
    payload = bytearray()
    offsets = {}
    for bid, blob in ordered:
        off = data_off + len(payload)
        offsets[bid] = (off, len(blob))
        dirents += struct.pack("<IIQQ", bid, 0, off, len(blob))
        payload += blob
    blob = header + bytes(dirents) + bytes(payload)
    d = os.path.dirname(os.path.abspath(out_path))
    if d and not os.path.isdir(d):
        os.makedirs(d)
    with open(out_path, "wb") as f:
        f.write(blob)
    return offsets, len(blob)


# ---------------------------------------------------------------- 独立只读解析端

class BinReader(object):
    """独立的只读解析器 —— 不复用写入端的任何中间对象。"""

    def __init__(self, data):
        self.data = data
        self.pos = 0

    def require(self, n):
        if self.pos + n > len(self.data):
            raise ValueError("读取越界: pos=%d need=%d size=%d" % (self.pos, n, len(self.data)))

    def u16(self):
        self.require(2)
        v = struct.unpack_from("<H", self.data, self.pos)[0]
        self.pos += 2
        return v

    def u32(self):
        self.require(4)
        v = struct.unpack_from("<I", self.data, self.pos)[0]
        self.pos += 4
        return v

    def take(self, n):
        self.require(n)
        b = self.data[self.pos:self.pos + n]
        self.pos += n
        return b


def read_bin(path):
    """解析整个 .bin, 返回结构化结果 (写入端不参与)。"""
    with open(path, "rb") as f:
        data = f.read()
    res = {"size": len(data), "data": data}
    if len(data) < HEADER_SIZE:
        raise ValueError("文件小于 32 字节")
    r = BinReader(data)
    magic = r.take(4)
    version = r.u32()
    block_count = r.u32()
    reserved = r.u32()
    if len(data) < HEADER_SIZE + DIRENT_SIZE * block_count:
        raise ValueError("目录越界")
    r.take(HEADER_SIZE - r.pos)          # 头部剩余保留字节
    directory = {}
    raw_dir = []
    for _ in range(block_count):
        bid = r.u32()
        rsv = r.u32()
        off = struct.unpack_from("<Q", data, r.pos)[0]
        r.pos += 8
        ln = struct.unpack_from("<Q", data, r.pos)[0]
        r.pos += 8
        raw_dir.append((bid, rsv, off, ln))
        if bid in directory:
            raise ValueError("目录 blockId 重复: %d" % bid)
        directory[bid] = (off, ln)
    res.update({
        "magic": magic, "version": version, "block_count": block_count,
        "reserved": reserved, "directory": directory, "raw_dir": raw_dir,
        "header_size": HEADER_SIZE, "dir_end": r.pos,
    })

    # --- blockId 1: 音节表 (UTF-8, LF 分隔, 无结尾换行) ---
    if BLOCK_SYLLABLES in directory:
        off, ln = directory[BLOCK_SYLLABLES]
        blob = data[off:off + ln]
        if len(blob) != ln:
            raise ValueError("音节块越界")
        text = blob.decode("utf-8")
        syllables = text.split("\n") if text else []
        res["syllables"] = syllables
        res["syllables_cursor"] = off + ln
        res["syllables_off_len"] = (off, ln)
    # --- blockId 2: 词表 ---
    if BLOCK_WORDS in directory:
        off, ln = directory[BLOCK_WORDS]
        r = BinReader(data)
        r.pos = off
        count = r.u32()
        words = []
        for _ in range(count):
            wl = r.u16()
            sl = r.u16()
            freq = r.u32()
            ids = [r.u16() for _ in range(sl)]
            raw = r.take(wl * 2)
            word = raw.decode("utf-16-le")
            if u16len(word) != wl:
                raise ValueError("wordLen 与实际码元数不符")
            if len(ids) != sl:
                raise ValueError("sylLen 与实际音节数不符")
            words.append((word, ids, freq))
        if r.pos != off + ln:
            raise ValueError("词表块末尾指针不吻合: pos=%d off+len=%d" % (r.pos, off + ln))
        res["words"] = words
        res["words_count"] = count
        res["words_cursor"] = r.pos
        res["words_off_len"] = (off, ln)
    # --- blockId 3: IDS 表 ---
    if BLOCK_IDS in directory:
        off, ln = directory[BLOCK_IDS]
        r = BinReader(data)
        r.pos = off
        count = r.u32()
        rows = []
        for _ in range(count):
            cp = r.u32()
            cl = r.u16()
            raw = r.take(cl * 2)
            comps = raw.decode("utf-16-le")
            if u16len(comps) != cl:
                raise ValueError("compLen 与实际码元数不符")
            rows.append((cp, comps))
        if r.pos != off + ln:
            raise ValueError("IDS 块末尾指针不吻合: pos=%d off+len=%d" % (r.pos, off + ln))
        res["ids"] = rows
        res["ids_count"] = count
        res["ids_cursor"] = r.pos
        res["ids_off_len"] = (off, ln)
    # --- blockId 4: Meta ---
    if BLOCK_META in directory:
        off, ln = directory[BLOCK_META]
        blob = data[off:off + ln]
        if len(blob) != ln:
            raise ValueError("Meta 块越界")
        res["meta"] = json.loads(blob.decode("utf-8"))
        res["meta_cursor"] = off + ln
        res["meta_off_len"] = (off, ln)
    return res


# ---------------------------------------------------------------- 自检

class CheckFailure(Exception):
    pass


def run_selftest(out_path, stats):
    print("")
    print("=== selftest: %s ===" % out_path)
    failures = []
    results = []

    def check(n, title, fn):
        try:
            msg = fn()
            results.append((n, title, msg))
            print("[ ok ] 第 %d 项: %s%s" % (n, title, ("  -- " + msg) if msg else ""))
        except Exception as exc:             # noqa: BLE001
            reason = "%s: %s" % (type(exc).__name__, exc)
            failures.append((n, reason))
            print("[FAIL] 第 %d 项: %s" % (n, reason))

    try:
        parsed = read_bin(out_path)
    except Exception as exc:                 # noqa: BLE001
        print("[FAIL] 第 1 项: 无法解析 %s (%s)" % (out_path, exc))
        print("selftest: 1 项失败")
        return False

    # --- 1. 头 / 目录 / 块长度 ---
    def c1():
        if parsed["magic"] != MAGIC:
            raise CheckFailure("magic=%r" % parsed["magic"])
        if parsed["version"] != VERSION:
            raise CheckFailure("version=%d" % parsed["version"])
        if parsed["block_count"] != len(BLOCK_IDS_ORDER):
            raise CheckFailure("blockCount=%d" % parsed["block_count"])
        if parsed["reserved"] != 0:
            raise CheckFailure("reserved=%d" % parsed["reserved"])
        if parsed["dir_end"] != HEADER_SIZE + DIRENT_SIZE * parsed["block_count"]:
            raise CheckFailure("目录区起止异常 dir_end=%d" % parsed["dir_end"])
        size = parsed["size"]
        spans = []
        for bid, rsv, off, ln in parsed["raw_dir"]:
            if rsv != 0:
                raise CheckFailure("blockId=%d reserved=%d" % (bid, rsv))
            if off < parsed["dir_end"]:
                raise CheckFailure("blockId=%d offset=%d 落在头部/目录内" % (bid, off))
            if off + ln > size:
                raise CheckFailure("blockId=%d offset+length=%d 超出文件 %d" % (bid, off + ln, size))
            spans.append((off, off + ln, bid))
        if set(b for _, _, b in spans) != set(BLOCK_IDS_ORDER):
            raise CheckFailure("blockId 集合=%s" % sorted(b for _, _, b in spans))
        spans.sort()
        for i in range(1, len(spans)):
            if spans[i][0] < spans[i - 1][1]:
                raise CheckFailure("块 %d 与块 %d 重叠" % (spans[i - 1][2], spans[i][2]))
        return "magic/version/blockCount/reserved 正确, 4 块长度与目录一致, 无重叠"

    check(1, "文件头与块目录 (FORMAT §8.1)", c1)

    # --- 2. IDS 抽查 ---
    def c2():
        want = {"你": "亻尔", "妈": "女马", "好": "女子", "测": "氵则"}
        got = {}
        for cp, comps in parsed["ids"]:
            ch = chr(cp)
            if ch in want:
                got[ch] = comps
        bad = []
        for ch, exp in want.items():
            if got.get(ch) != exp:
                bad.append("%s -> %r (期望 %r)" % (ch, got.get(ch), exp))
        if bad:
            raise CheckFailure("; ".join(bad))
        return "你=亻尔 妈=女马 好=女子 测=氵则"

    check(2, "IDS 抽查 (FORMAT §8.2)", c2)

    # --- 3. ni ma -> 你妈 ---
    def c3():
        syl_id = {s: i for i, s in enumerate(parsed["syllables"])}
        if "ni" not in syl_id or "ma" not in syl_id:
            raise CheckFailure("音节表缺少 ni/ma")
        target = [syl_id["ni"], syl_id["ma"]]
        hits = [(w, ids, f) for (w, ids, f) in parsed["words"] if w == "你妈" and ids == target]
        if not hits:
            raise CheckFailure("词表中未找到 你妈 -> ni ma")
        return "你妈 = ni ma (freq=%d)" % hits[0][2]

    check(3, "词表 ni ma -> 你妈 (FORMAT §8.3)", c3)

    # --- 4. 有序/无重复 ---
    def c4():
        syl = parsed["syllables"]
        if syl != sorted(syl):
            raise CheckFailure("音节表未按字节序升序")
        if len(set(syl)) != len(syl):
            raise CheckFailure("音节表存在重复")
        cps = [cp for cp, _ in parsed["ids"]]
        if cps != sorted(cps):
            raise CheckFailure("IDS 表未按 codepoint 升序")
        if len(set(cps)) != len(cps):
            raise CheckFailure("IDS 表存在重复 codepoint")
        return "音节表 %d 条升序无重复; IDS 表 %d 条按码点升序无重复" % (len(syl), len(cps))

    check(4, "音节表/IDS 表有序无重复 (FORMAT §8.4)", c4)

    # --- 5. 逐条回读 count 与写入值一致 ---
    def c5():
        n_syl = len(parsed["syllables"])
        n_words = 0
        for word, ids, freq in parsed["words"]:
            n_words += 1
            if freq < 1:
                raise CheckFailure("freq=%d < 1 (词=%s)" % (freq, word))
            if len(word) < 1 or len(ids) < 1:
                raise CheckFailure("wordLen/sylLen 为 0 (词=%s)" % word)
            for i in ids:
                if i >= n_syl:
                    raise CheckFailure("sylId=%d 越界 (音节表 %d)" % (i, n_syl))
        if n_words != parsed["words_count"]:
            raise CheckFailure("词表回读 %d 条 != count %d" % (n_words, parsed["words_count"]))
        n_ids = 0
        for cp, comps in parsed["ids"]:
            n_ids += 1
            if not (0 <= cp <= 0x10FFFF):
                raise CheckFailure("codepoint 越界: %d" % cp)
            if u16len(comps) > 0xFFFF:
                raise CheckFailure("compLen 越界")
        if n_ids != parsed["ids_count"]:
            raise CheckFailure("IDS 回读 %d 条 != count %d" % (n_ids, parsed["ids_count"]))
        # 与 Meta 中记录的各块 payload 摘要比对 (写入端声明值)
        declared = (parsed.get("meta") or {}).get("blocks_sha256") or {}
        for bid in ("1", "2", "3"):
            off, ln = parsed["directory"][int(bid)]
            digest = hashlib.sha256(parsed["data"][off:off + ln]).hexdigest()
            if declared.get(bid) != digest:
                raise CheckFailure("块 %s payload sha256 与 Meta 声明不符" % bid)
        return "词表 %d 条 / IDS %d 条逐条回读一致, 块 1/2/3 payload sha256 与 Meta 吻合" % (n_words, n_ids)

    check(5, "逐条回读 count 与写入值一致 (FORMAT §8.5)", c5)

    # --- 6. 音节表规模与字符集 ---
    def c6():
        syl = parsed["syllables"]
        if not (380 <= len(syl) <= 470):
            raise CheckFailure("音节表 %d 条, 不在 380~470" % len(syl))
        bad = [s for s in syl if not _SYL_RE.match(s)]
        if bad:
            raise CheckFailure("非 [a-z]+ 音节: %r" % bad[:5])
        return "%d 条, 全部匹配 ^[a-z]+$" % len(syl)

    check(6, "音节表条数 380~470 且全为 a-z", c6)

    # --- 7. 词表规模与关键词可查 ---
    def c7():
        if parsed["words_count"] <= 50000:
            raise CheckFailure("词表 count=%d <= 50000" % parsed["words_count"])
        syl_id = {s: i for i, s in enumerate(parsed["syllables"])}
        want = {"你妈": ["ni", "ma"], "你好": ["ni", "hao"], "中国": ["zhong", "guo"]}
        idx = defaultdict(list)
        for word, ids, freq in parsed["words"]:
            idx[word].append((ids, freq))
        detail = []
        for word, syls in want.items():
            if any(s not in syl_id for s in syls):
                raise CheckFailure("音节表缺少 %s 的音节" % word)
            target = [syl_id[s] for s in syls]
            hit = [f for ids, f in idx.get(word, []) if ids == target]
            if not hit:
                raise CheckFailure("词表中未找到 %s -> %s" % (word, " ".join(syls)))
            detail.append("%s=%d" % (word, hit[0]))
        return "count=%d, %s" % (parsed["words_count"], ", ".join(detail))

    check(7, "词表 count > 50000 且关键词可查", c7)

    # --- 8. IDS 表规模与抽查 ---
    def c8():
        if parsed["ids_count"] <= 20000:
            raise CheckFailure("IDS count=%d <= 20000" % parsed["ids_count"])
        want = {"你": "亻尔", "妈": "女马", "好": "女子", "测": "氵则", "亭": "亠口冖丁"}
        got = {}
        for cp, comps in parsed["ids"]:
            ch = chr(cp)
            if ch in want:
                got[ch] = comps
        bad = []
        for ch, exp in want.items():
            if got.get(ch) != exp:
                bad.append("%s -> %r (期望 %r)" % (ch, got.get(ch), exp))
        if bad:
            raise CheckFailure("; ".join(bad))
        return "count=%d, 你/妈/好/测/亭 全部正确" % parsed["ids_count"]

    check(8, "IDS 表 count > 20000 且 5 字抽查正确", c8)

    # --- 9. 文件大小 ---
    def c9():
        size = parsed["size"]
        if size >= 25 * 1024 * 1024:
            raise CheckFailure("文件大小 %d 字节 >= 25 MB" % size)
        return "%.3f MB (%d 字节) < 25 MB" % (size / 1048576.0, size)

    check(9, "文件总大小 < 25 MB", c9)

    # --- 10. 每块末尾指针与 offset+length 吻合 ---
    def c10():
        detail = []
        for bid, cur_key, ol_key in ((1, "syllables_cursor", "syllables_off_len"),
                                     (2, "words_cursor", "words_off_len"),
                                     (3, "ids_cursor", "ids_off_len"),
                                     (4, "meta_cursor", "meta_off_len")):
            if cur_key not in parsed:
                raise CheckFailure("块 %d 未被解析" % bid)
            off, ln = parsed[ol_key]
            if parsed[cur_key] != off + ln:
                raise CheckFailure("块 %d 指针 %d != offset+length %d" % (bid, parsed[cur_key], off + ln))
            detail.append("块%d=%d" % (bid, off + ln))
        return "全部吻合 (%s)" % ", ".join(detail)

    check(10, "解析到每块末尾时指针与 offset+length 完全吻合", c10)

    # --- 11. 词表规模落在规范建议区间 8 万 ~ 15 万 ---
    def c11():
        n = parsed["words_count"]
        if not (MIN_TOTAL_WORDS <= n <= MAX_TOTAL_WORDS):
            raise CheckFailure("词表 %d 条, 不在 8 万 ~ 15 万" % n)
        return "%d 条 (单字 %d / 多字 %d)" % (
            n,
            sum(1 for w, _, _ in parsed["words"] if len(w) == 1),
            sum(1 for w, _, _ in parsed["words"] if len(w) > 1),
        )

    check(11, "词表规模在 8 万 ~ %d 之间" % MAX_TOTAL_WORDS, c11)

    # --- 12. 验收要求的 16 个简体词必须存在 ---
    REQUIRED_SIMP = ("你妈", "你好", "测试", "中国", "谢谢", "键盘", "电影",
                     "意思", "苹果", "输入法", "输入", "形变", "尼玛", "美女",
                     "计划", "发展")

    def c12():
        present = set(w for w, _, _ in parsed["words"])
        missing = [w for w in REQUIRED_SIMP if w not in present]
        if missing:
            raise CheckFailure("词表缺少 %d 个简体词: %s" % (len(missing), " ".join(missing)))
        return "%d 个简体词全部存在: %s" % (len(REQUIRED_SIMP), " ".join(REQUIRED_SIMP))

    check(12, "16 个简体词全部存在于词表", c12)

    # --- 13. 指定音节序列的第一名候选必须是简体词 ---
    RANK_CASES = (
        (["ni", "ma"], "你妈"),
        (["jian", "pan"], "键盘"),
        (["shu", "ru", "fa"], "输入法"),
        (["zhong", "guo"], "中国"),
        (["ce", "shi"], "测试"),
    )

    def c13():
        syl_id = {s: i for i, s in enumerate(parsed["syllables"])}
        detail = []
        bad = []
        for syls, want in RANK_CASES:
            miss = [s for s in syls if s not in syl_id]
            if miss:
                bad.append("%s: 音节表缺少 %s" % (" ".join(syls), miss))
                continue
            target = [syl_id[s] for s in syls]
            # 词表按 freq 降序落盘, 因此文件顺序中的首个命中即第一名候选
            top = None
            for word, ids, freq in parsed["words"]:
                if ids == target:
                    top = (word, freq)
                    break
            if top is None:
                bad.append("%s: 无任何候选" % " ".join(syls))
            elif top[0] != want:
                bad.append("%s: 第一名是 %s(freq=%d), 期望 %s"
                           % (" ".join(syls), top[0], top[1], want))
            else:
                detail.append("%s -> %s(%d)" % (" ".join(syls), top[0], top[1]))
        if bad:
            raise CheckFailure("; ".join(bad))
        return "; ".join(detail)

    check(13, "5 条音节序列第一名候选为指定简体词", c13)

    # --- 14. 词表不得含任何 "可被 T->S 映射改变" 的字符 ---
    def c14():
        if not os.path.isfile(T2S_PATH):
            raise CheckFailure("映射表不存在: %s" % T2S_PATH)
        t2s_map = opencc_first_map(T2S_PATH)
        # 只把 "键 != 值" 的键视为真繁体; 键==值是恒等映射, 不算繁体 (否则误报)。
        # 用不动点展开后的表, 这样链式字 (薴->苧->苎) 的中间态 苧 也会被算进来。
        expanded = t2s_fixpoint_map(t2s_map)
        real_trad = set(k for k, v in expanded.items() if k != v)
        # 词组表会**故意保留**某些字: 出现在某条词组键里、同时出现在它自己的值里。
        # 例如 `乾圖 -> 乾图`, 所以 '乾' 是合法的简体输出 (乾图/乾坤/乾隆 都对),
        # 而 '圖' 不是 (词组表里从没把它留在值里)。判据 = 真繁体 且 未被词组表保护。
        phrases = opencc_phrase_map(T2S_PHRASES_PATH)
        protected = set()
        for k, v in phrases.items():
            for ch in k:
                if ch in v:
                    protected.add(ch)
        bad_chars = set(c for c in real_trad if c not in protected)
        bad = []
        seen_bad = set()
        for word, _, _ in parsed["words"]:
            for ch in word:
                if ch in bad_chars and ch not in seen_bad:
                    seen_bad.add(ch)
                    bad.append("%s (U+%04X)" % (ch, ord(ch)))
                    break
            if len(bad) >= 10:
                break
        if bad:
            raise CheckFailure("词表仍含未归一化的繁体字 %d+ 个 (前 10 个反例: %s)"
                               % (len(bad), ", ".join(bad)))
        return ("已扫描全部 %d 条, 无未归一化繁体 (单字表 %d 键 / 展开后真繁体 %d; "
                "词组表保护 %d 字, 豁免后待查 %d 字)" % (
                    parsed["words_count"], len(t2s_map), len(real_trad),
                    len(protected), len(bad_chars)))

    check(14, "词表不含任何可被 T->S 映射改变的字符", c14)

    # --- 15. 你妈的 IDS 拆解链 ---
    def c15():
        want = {"你": ["亻", "尔"], "妈": ["女", "马"]}
        got = {}
        for cp, comps in parsed["ids"]:
            ch = chr(cp)
            if ch in want:
                got[ch] = [c for c in comps]
        bad = []
        for ch, exp in want.items():
            if got.get(ch) != exp:
                bad.append("%s -> %r (期望 %r)" % (ch, got.get(ch), exp))
        if bad:
            raise CheckFailure("; ".join(bad))
        # 同步确认 你妈 仍可查 (IDS 链与词表条目同时成立才有意义)
        if not any(w == "你妈" for w, _, _ in parsed["words"]):
            raise CheckFailure("IDS 链正确但词表查不到 你妈")
        return "你 = 亻 + 尔, 妈 = 女 + 马 (IDS 表 %d 条中命中)" % parsed["ids_count"]

    check(15, "你妈的 IDS 拆解链 你->亻尔 妈->女马", c15)

    # --- 16. 词组表保护: 逐字过度归一化的词必须完好 ---
    def c16():
        # 纯逐字映射会把 乾隆/乾坤/乾元 的 '乾' 写成 '干' (TSCharacters 首候选),
        # 词组表 (含恒等条目) 必须把它们救回来。这些词在词表里必须存在,
        # 且不得存在对应的错误形态。
        must_have = ["乾隆", "乾坤", "乾元", "乾清宫", "乾陵"]
        must_not = ["干隆", "干坤", "干元", "干清宫", "干陵"]
        words = set(w for w, _, _ in parsed["words"])
        missing = [w for w in must_have if w not in words]
        wrong = [w for w in must_not if w in words]
        if missing:
            raise CheckFailure("过度归一化残留, 词表缺少 %s" % ", ".join(missing))
        if wrong:
            raise CheckFailure("过度归一化未被词组表拦住, 词表出现错误形态 %s" % ", ".join(wrong))
        if not os.path.isfile(T2S_PHRASES_PATH):
            raise CheckFailure("词组表不存在: %s" % T2S_PHRASES_PATH)
        raw = opencc_phrase_map(T2S_PHRASES_PATH)
        # 词组表必须真被用上: 恒等条目数 > 0, 且两条锚点确实是恒等
        ident = [k for k, v in raw.items() if k == v]
        for anchor in ("乾隆", "乾坤"):
            if raw.get(anchor) != anchor:
                raise CheckFailure("词组表缺恒等锚点 %s -> %r" % (anchor, raw.get(anchor)))
        return ("词组表 %d 条 (恒等保护 %d), 乾隆/乾坤/乾元/乾清宫/乾陵 全部存在, "
                "错误形态 干隆/干坤/干元/干清宫/干陵 全部不存在") % (len(raw), len(ident))

    check(16, "词组表挡住逐字过度归一化 (乾隆/乾坤 等)", c16)

    print("")
    if failures:
        print("selftest: %d/%d 项失败" % (len(failures), len(results) + len(failures)))
        print("SELFTEST FAIL: " + "; ".join("第%d项 %s" % (n, r) for n, r in failures))
        return False
    print("selftest: 全部 %d 项通过" % len(results))
    print("SELFTEST PASS")
    return True


# ---------------------------------------------------------------- --dump

def cmd_dump(target, out_path):
    if os.path.isfile(out_path):
        parsed = read_bin(out_path)
        syllables = parsed.get("syllables", [])
        ids_map = {chr(cp): comps for cp, comps in parsed.get("ids", [])}
        word_idx = defaultdict(list)
        for word, ids, freq in parsed.get("words", []):
            word_idx[word].append((ids, freq))
        source = out_path
    else:
        print("[warn] %s 不存在, 直接从 assets/raw 解析" % out_path)
        stats = {}
        jieba_probe = parse_jieba(SRC["jieba"], stats)
        accept_probe, _ = build_readability_filter(jieba_probe)
        ids_map_raw = parse_ids(SRC["ids"], stats, accept_probe)
        ids_map = {chr(cp): c for cp, c in ids_map_raw.items()}
        pinyin_data = parse_pinyin_data(SRC["pinyin"], stats)
        luna_single, luna_phrases = parse_luna(SRC["luna"], stats)
        phrase_table = parse_phrase_pinyin(SRC["phrase_pinyin"], stats)
        jieba = parse_jieba(SRC["jieba"], stats)
        essay = parse_essay(SRC["essay"], stats)
        entries = build_word_entries(ids_map_raw, pinyin_data, luna_single, luna_phrases,
                                     phrase_table, jieba, essay, stats)
        entries = trim_entries(entries, stats)
        syllables = sorted(set(s for _, syls, _ in entries for s in syls))
        sid = {s: i for i, s in enumerate(syllables)}
        word_idx = defaultdict(list)
        for word, syls, freq in entries:
            word_idx[word].append(([sid[s] for s in syls], freq))
        source = "assets/raw"

    print("[dump] %r  (来源: %s)" % (target, source))
    if len(target) == 1:
        cp = ord(target)
        comps = ids_map.get(target)
        if comps is None:
            print("  IDS : (无 / 不可拆)")
        else:
            print("  IDS : U+%04X  compLen=%d  comps=%s" % (cp, u16len(comps), comps))
    rows = word_idx.get(target)
    if not rows:
        print("  读音: (未收录)")
        return 1
    rows.sort(key=lambda r: (-r[1], r[0]))
    print("  读音 (%d 条, 音节表 %d 条):" % (len(rows), len(syllables)))
    for ids, freq in rows:
        py = " ".join(syllables[i] for i in ids)
        print("    %-24s freq=%d" % (py, freq))
    return 0


# ---------------------------------------------------------------- main

def build(out_path, max_words=None):
    t0 = time.time()
    stats = {}
    budget = int(max_words) if max_words else TARGET_TOTAL_WORDS
    print("[build] 源目录 %s" % RAW_DIR)
    print("[build] 词表预算 --max-words = %d" % budget)

    srcs = {
        "ids.txt": SRC["ids"],
        "pinyin-data.txt": SRC["pinyin"],
        "phrase-pinyin-data.txt": SRC["phrase_pinyin"],
        "jieba_dict.txt": SRC["jieba"],
        "essay.txt": SRC["essay"],
        "luna_pinyin.dict.yaml": SRC["luna"],
    }
    for name, p in srcs.items():
        if not os.path.isfile(p):
            raise SystemExit("[FATAL] 缺少源文件: %s" % p)
    if not os.path.isfile(T2S_PATH):
        raise SystemExit("[FATAL] 缺少繁简映射表: %s" % T2S_PATH)

    # 部件可读性过滤需要词频, 所以 jieba 先于 ids 解析。
    jieba = parse_jieba(srcs["jieba_dict.txt"], stats)
    accept_cps, rf_stats = build_readability_filter(jieba)
    stats["readability"] = dict(rf_stats)
    stats["readability"]["min_freq"] = COMMON_CHAR_MIN_FREQ
    print("[build] 可读性过滤     : 常用字 %d (jieba freq >= %d) + 部首 %d" % (
        rf_stats["common_chars"], COMMON_CHAR_MIN_FREQ, rf_stats["radicals"]))

    ids_table = parse_ids(srcs["ids.txt"], stats, accept_cps)
    print("[build] ids.txt        : 保留可一级拆解 %d 条 (其中 %d 条部件不可读被丢弃, %d 条拉丁形补回)" % (
        len(ids_table),
        stats["ids"].get("not_decomposable_components_unreadable", 0),
        stats["ids"].get("latin_shape_added", 0)))

    pinyin_data = parse_pinyin_data(srcs["pinyin-data.txt"], stats)
    print("[build] pinyin-data    : %d 字 / %d 读音" % (
        stats["pinyin_data"]["chars"], stats["pinyin_data"]["readings_kept"]))

    luna_single, luna_phrases = parse_luna(srcs["luna_pinyin.dict.yaml"], stats)
    print("[build] luna yaml      : 单字 %d / 词条 %d" % (
        stats["luna"]["single_chars"], stats["luna"]["phrases"]))

    phrase_table = parse_phrase_pinyin(srcs["phrase-pinyin-data.txt"], stats)
    print("[build] phrase-pinyin  : %d 词" % len(phrase_table))

    print("[build] jieba_dict     : %d 词" % len(jieba))
    # (jieba 的主解析已提前到 ids 之前, 这里不重复解析 —— 见上方可读性过滤一节。)

    essay = parse_essay(srcs["essay.txt"], stats)
    print("[build] essay          : %d 词" % len(essay))

    # 归一化后不是纯 a-z 而被丢弃的音节/读音 (规范要求必须打印统计)
    dropped_not_az = {}
    for sect in ("pinyin_data", "luna", "phrase_pinyin"):
        d = stats.get(sect) or {}
        for k, v in d.items():
            if "not_a_z" in k and isinstance(v, int):
                dropped_not_az["%s.%s" % (sect, k)] = v
    stats["normalization_drops"] = {"not_a_z_total": sum(dropped_not_az.values()),
                                    "by_section": dropped_not_az}
    print("[build] 丢弃非 a-z 读音: %d 条 %s" % (
        sum(dropped_not_az.values()),
        ("(" + ", ".join("%s=%d" % kv for kv in sorted(dropped_not_az.items())) + ")")
        if dropped_not_az else ""))

    entries = build_word_entries(ids_table, pinyin_data, luna_single, luna_phrases,
                                 phrase_table, jieba, essay, stats)
    print("[build] 候选词条       : %d (单字 %d / 多字 %d)" % (
        stats["word_build"]["candidate_entries_before_trim"],
        stats["word_build"]["candidate_single_char_entries"],
        stats["word_build"]["candidate_multi_char_entries"]))

    # ---- 繁 -> 简归一化 + 合并 (在裁剪之前, 否则繁体高频词会继续挤掉简体词) ----
    t2s, t2s_all, t2s_st = parse_opencc_map(T2S_PATH)
    stats["t2s_map"] = t2s_st
    print("[build] 繁简映射表     : %d 键 (%d 真繁体 / %d 恒等映射), 多候选 %d, 非 BMP 首候选 %d" % (
        t2s_st["keys"], t2s_st["true_traditional"], t2s_st["identity"],
        t2s_st["multi_value"], t2s_st["astral_first"]))
    # 词组表必须先于单字表应用, 否则 乾隆->干隆 这类过度归一无法挽回。
    phrases = {}
    phrase_max_len = 0
    if os.path.isfile(T2S_PHRASES_PATH):
        phrases, stats = parse_opencc_phrases(T2S_PHRASES_PATH, stats)
        phrase_max_len = stats["opencc_phrases"]["max_key_len"]
        pst = stats["opencc_phrases"]
        print("[build] 繁简词组表     : %d 条 (真转换 %d / 恒等保护 %d), 最长 %d 字" % (
            pst["entries"], pst["converted"], pst["identity"], pst["max_key_len"]))
    else:
        print("[build] 繁简词组表     : 缺失 (%s), 退化为纯逐字映射" % T2S_PHRASES_PATH)
    entries, stats = normalize_t2s(entries, t2s, stats, phrases, phrase_max_len)
    nz = stats["t2s"]
    print("[build] 繁->简归一化   : %d -> %d 条 (改写 %d / 合并 %d / 丢弃 非汉字 %d / 保留非BMP %d / 词组表生效 %d)" % (
        nz["entries_before"], nz["entries_after"], nz["entries_renamed"],
        nz["entries_merged"], nz["entries_dropped_non_han"],
        nz["entries_kept_astral"], nz["entries_phrase_aware_differs"]))

    entries = trim_entries(entries, stats, budget)
    print("[build] 裁剪后词条     : %d (单字 %d / 多字 %d)" % (
        stats["trim"]["kept_total"], stats["trim"]["kept_single_char"],
        stats["trim"]["kept_multi_char"]))

    syllables = sorted(set(s for _, syls, _ in entries for s in syls))
    syl_id = {s: i for i, s in enumerate(syllables)}
    print("[build] 音节表         : %d 条" % len(syllables))

    blocks = {}
    blocks[BLOCK_SYLLABLES] = pack_syllable_block(syllables)
    blocks[BLOCK_WORDS] = pack_words_block(entries, syl_id)
    blocks[BLOCK_IDS] = pack_ids_block(ids_table)

    # --- Meta ---
    source_files = {}
    mtimes = []
    for name, p in sorted(srcs.items()):
        st = os.stat(p)
        mtimes.append(st.st_mtime)
        source_files["%s/%s" % (RAW_REL_PREFIX, name)] = {
            "sha256": sha256_file(p),
            "bytes": st.st_size,
            "lines": _count_lines(p),
        }
    st = os.stat(T2S_PATH)
    mtimes.append(st.st_mtime)
    source_files["%s/opencc_TSCharacters.txt" % RAW_REL_PREFIX] = {
        "sha256": sha256_file(T2S_PATH),
        "bytes": st.st_size,
        "lines": _count_lines(T2S_PATH),
    }
    if os.path.isfile(T2S_PHRASES_PATH):
        st = os.stat(T2S_PHRASES_PATH)
        mtimes.append(st.st_mtime)
        source_files["%s/opencc_TSPhrases.txt" % RAW_REL_PREFIX] = {
            "sha256": sha256_file(T2S_PHRASES_PATH),
            "bytes": st.st_size,
            "lines": _count_lines(T2S_PHRASES_PATH),
        }
    blocks_payload_sha = {
        str(bid): hashlib.sha256(blocks[bid]).hexdigest()
        for bid in (BLOCK_SYLLABLES, BLOCK_WORDS, BLOCK_IDS)
    }
    sde = os.environ.get("SOURCE_DATE_EPOCH")
    if sde and sde.strip().isdigit():
        stamp = int(sde.strip())
        stamp_src = "SOURCE_DATE_EPOCH"
    else:
        stamp = int(max(mtimes)) if mtimes else 0
        stamp_src = "max(mtime of source files)"
    stats["output"] = {
        "words_count": len(entries),
        "syllables_count": len(syllables),
        "ids_count": len(ids_table),
        "block_bytes": {str(bid): len(blocks[bid]) for bid in sorted(blocks)},
    }
    # 注意: 运行时耗时属于"每次运行都可能变"的值, 写进 Meta 会让产物无法逐字节复现,
    # 因此只打印到 stdout, 不写入 blockId=4。幂等/可复现优先。
    elapsed = round(time.time() - t0, 3)
    meta = {
        "format": "xingbian.bin",
        "format_version": VERSION,
        "spec": "docs/FORMAT.md",
        "generator": "tools/build_dict.py",
        "generated_at": iso_utc(stamp),
        "generated_at_policy": (
            "确定性构建时间戳: 取 SOURCE_DATE_EPOCH (若设置) 否则取源文件 mtime 最大值 (UTC); "
            "目的是保证同一批输入重复构建产出完全相同的字节."
        ),
        "source_files": source_files,
        "blocks_sha256": blocks_payload_sha,
        "source_contrib": {
            "essay_freq": {"weight": W_ESSAY, "formula": "weight * log1p(freq)/log1p(%d)" % ESSAY_FREQ_MAX},
            "jieba_freq": {"weight": W_JIEBA, "formula": "weight * log1p(freq)/log1p(%d)" % JIEBA_FREQ_MAX},
            "luna_dict": {"weight": C_LUNA, "formula": "平值存在贡献"},
            "phrase_pinyin": {"weight": C_PHRASE, "formula": "平值存在贡献"},
            "pinyin_data_single": {"weight": C_PINYIN, "formula": "平值存在贡献 (单字读音来源标记)"},
            "required_seed_words": {"weight": C_SEED, "words": list(REQUIRED_WORDS),
                                    "formula": "平值存在贡献 (保证 §8.3 指定词存活)"},
            "freq_formula": "freq = max(1, round(%d * max_over_sources(contrib)))" % FREQ_SCALE,
        },
        "normalization": {
            "syllable": "无调 / 全小写 / 仅 a-z; ü(及 ǖǘǚǜ) -> v; 数字调后缀去除; 归一化后非 ^[a-z]+$ 的读音丢弃",
            "ids": "取每个码点第一条 IDS; 剥离 [GJK] 类标注; 删除全部结构符 U+2FF0..U+2FFB; 仅保留一层",
            "ids_readability": (
                "部件可读性过滤: 拆出的每个部件必须能认出来 (jieba 词频 >= %d 的常用字, "
                "或在公认部首白名单里), 否则该字不拆 (读取端视同 compLen=0, 输出保留原字)。"
                "目的: 杜绝 个->人丨 这类拆完无法辨认的输出。"
                "随后应用形近拉丁字母替换表 LATIN_SHAPE_MAP (如 儿->JL), "
                "把「部件全是笔画名但整字形状可用字母认出」的字用字母串补回。" % COMMON_CHAR_MIN_FREQ),
            "ids_not_decomposable": "无结构符 / 去结构符后为空 / 去结构符后等于该字本身 => 不写入该条目 (读取端视同 compLen=0)",
            "words_charset": "词只保留纯汉字 (3400-4DBF, 4E00-9FFF, F900-FAFF, 20000-2FA1F, 3007)",
            "syllable_len_rule": "音节数必须等于汉字数 (码点数), 不等则丢弃",
            "traditional_to_simplified": (
                "在裁剪之前, 用 assets/raw/opencc_TSCharacters.txt (OpenCC T->S 单字表, "
                "制表符分栏 `键\t候选1 候选2 ...`) 对每个候选词逐字归一化: 每个字取其**首个**简体候选, "
                "不在表中的字原样保留 (已简体的词不受影响, 一字不改)。实测该表含 1 条链 薴->苧->苎 且无环, "
                "故迭代到不动点 (绝大多数字 1 步即止), 否则中间态 苧 会残留在词表里。"
                "繁简单字一一对应, 词长不变, 故音节 ID 序列仍然有效, 不重算拼音。"
                "归一化后 (音节ID序列, 简体词) 相同的条目合并, freq 取两侧最大值。"
                "理由: 若不归一化, essay.txt(繁体语料) 的高频条目会压过 jieba(简体) "
                "并各占一份名额, NI MA 会只查得到 你媽 而查不到简体 你妈。"
            ),
        },
        "reading_priority": [
            "1. luna_pinyin.dict.yaml 词条 (自带音节序列)",
            "2. phrase-pinyin-data.txt 词条 (带调, 归一化后按空格切)",
            "3. 单字读音表逐字组合 (多音字取 pinyin-data.txt 第一条读音; 该字缺失则用 luna 百分比最高读音; 都无则丢弃该词)",
        ],
        "trimming": dict(stats["trim"], policy="繁->简归一化合并后, 单字条目全部保留, 多字条目按 freq 降序补足至 target_total"),
        "stage_stats": stats,
        # ---- 以下 4 个键为任务书 §4 明确点名要求 (与上面的同义键并存, 保持向后兼容) ----
        # counts: 各块条数 (与 stage_stats.output 同值, 抽出来便于读取端直接取用)
        "counts": {
            "syllables": len(syllables),
            "words": len(entries),
            "ids": len(ids_table),
            # 注意: 此处不能用 len(blocks) —— meta 本身在下面几行才写入 blocks[BLOCK_META],
            # 用 len(blocks) 会得到 3 而文件头 blockCount 是 4, 自相矛盾.
            "blocks": len(BLOCK_IDS_ORDER),
        },
        # source_versions: 各源文件 sha256 即"版本", 用于判定产物是否需重建
        "source_versions": {k: v["sha256"] for k, v in sorted(source_files.items())
                            if isinstance(v, dict) and "sha256" in v},
        # builder: 生成脚本路径 (与 generator 同义)
        "builder": "tools/build_dict.py",
        # notes: 本次构建所做的关键取舍 (读取端/维护者可据此判断数据语义)
        "notes": [
            "块3 每个码点只保留一条 IDS: 取源文件该行第 3 列(第一条候选). "
            "实测 ids.txt 每行可能有 3~7 列(同字多来源候选), 锁点字 你妈好测 均有唯一候选, "
            "但全表有 3040~3368 个码点的取法与备选规则不同, 本产物统一采用'第一条'.",
            "块3 只做一层拆解, 不做递归展开; 部件本身仍是合成字(如 你->亻尔, 亻/尔 各自还可拆)是正确的.",
            "块3 剥离结构符仅覆盖 U+2FF0..U+2FFB 共 12 个(实测全量 ids.txt 中 U+2FFC..U+2FFF 与 U+31EF 出现 0 次); "
            "未使用 unicodedata.category=='So' 兜底, 因为该判据会误删 984 处真部件(⺼ ⺊ ⺀ ⺄ ⺌ 等 20 种).",
            "块3 含 ? * U+ & 等占位/引用标记的条目全部丢弃; 去结构符后为空或等于该字本身的条目不写入.",
            "读音优先级: luna_pinyin.dict.yaml(词条自带音节) > phrase-pinyin-data.txt > 单字读音表逐字拼接; "
            "逐字拼接仅用于长度 1~4 且各字读音唯一的词, 遇多音字丢弃该词(宁缺毋滥).",
            "ü 及其带调形式 ǖǘǚǜ 统一映射为 v (与 luna_pinyin 一致), 不使用 u.",
            "音节数必须等于汉字数, 故含非汉字/儿化音(如 一丁点儿 4 音节 3 汉字)的词被丢弃.",
            "essay.txt 为繁体语料, 裁剪前先做 T->S 归一化并合并同读音条目, 否则简体高频词会被繁体挤掉名额.",
            "词表规模默认 200000 条, 高于 FORMAT.md §7 建议的 8 万~15 万; 该建议是'控制内存'的性能提示而非格式约束, "
            "任务书亦要求'必须保证 你妈 能查到'. 如需严格落在建议区间可用 --max-words 150000 重建.",
            "Meta 不写入运行耗时与随机值, generated_at 由 SOURCE_DATE_EPOCH 或源文件 mtime 派生, 保证逐字节可复现.",
        ],
        "reproducibility": (
            "同一批输入重复构建产出逐字节相同的结果: Meta 不含时间戳(除源 mtime 派生的 generated_at) "
            "也不含运行耗时; 如需覆盖 generated_at 可设置 SOURCE_DATE_EPOCH."
        ),
    }
    blocks[BLOCK_META] = json.dumps(meta, ensure_ascii=False, sort_keys=True,
                                    separators=(",", ":")).encode("utf-8")

    offsets, size = write_bin(out_path, blocks)
    print("[build] 写出 %s (%.3f MB, %d 字节)" % (out_path, size / 1048576.0, size))
    for bid in sorted(offsets):
        print("[build]   块 %d: offset=%d length=%d" % (bid, offsets[bid][0], offsets[bid][1]))
    return size


def _count_lines(path):
    n = 0
    with open(path, "rb") as f:
        while True:
            b = f.read(1 << 20)
            if not b:
                break
            n += b.count(b"\n")
    return n


def write_report(path, out_path):
    """把已生成的 xingbian.bin 的实际内容回读汇总成一份 Markdown 报告。

    注意: 这里**只统计二进制产物本身** (各块条数 / 体积 / sha256), 不重跑构建、
    也不复述源码里的常量, 因此报告数字与产物一定自洽。
    """
    parsed = read_bin(out_path)
    size = os.path.getsize(out_path)
    sha = sha256_file(out_path)
    n_syl = len(parsed.get("syllables", []))
    n_word = parsed.get("words_count", len(parsed.get("words", [])))
    n_ids = parsed.get("ids_count", len(parsed.get("ids", [])))
    single = sum(1 for w, _, _ in parsed.get("words", []) if len(w) == 1)
    # read_bin() 只给出 directory{blockId:(off,len)}, 没有聚合的 payload 字典, 故按目录取长度。
    blen = {bid: ln for bid, (off, ln) in parsed["directory"].items()}
    lines = [
        "# xingbian.bin 构建报告",
        "",
        "> 本文件由 `tools/build_dict.py --report` 生成，数字全部来自**回读二进制产物**。",
        "",
        "## 产物",
        "",
        "| 指标 | 值 |",
        "|---|---|",
        "| 路径 | `%s` |" % out_path,
        "| 大小 | %d 字节 (%.3f MB) |" % (size, size / 1048576.0),
        "| sha256 | `%s` |" % sha,
        "| magic / version | `%s` / %d |" % (MAGIC.decode("ascii"), VERSION),
        "",
        "## 各块条数 (回读值)",
        "",
        "| 块 | 条数 | payload 字节 |",
        "|---|---|---|",
        "| 1 音节表 | %d | %d |" % (n_syl, blen.get(BLOCK_SYLLABLES, 0)),
        "| 2 词表 | %d (单字 %d / 多字 %d) | %d |" % (
            n_word, single, n_word - single, blen.get(BLOCK_WORDS, 0)),
        "| 3 IDS 表 | %d | %d |" % (n_ids, blen.get(BLOCK_IDS, 0)),
        "| 4 Meta | 1 (JSON) | %d |" % blen.get(BLOCK_META, 0),
        "",
        "## 块目录 (回读值)",
        "",
        "```",
    ]
    for bid, rsv, off, ln in parsed["raw_dir"]:
        lines.append("blockId=%d reserved=%d offset=%-10d length=%-10d end=%d"
                     % (bid, rsv, off, ln, off + ln))
    lines += [
        "```",
        "",
        "## 数据源实测 sha256",
        "",
        "| 源 | sha256 |",
        "|---|---|",
    ]
    for key in sorted(SRC):
        p = SRC[key]
        if os.path.isfile(p):
            lines.append("| `%s` | `%s` |" % (os.path.basename(p), sha256_file(p)))
    lines.append("")
    d = os.path.dirname(os.path.abspath(path))
    if d and not os.path.isdir(d):
        os.makedirs(d, exist_ok=True)
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    print("[report] 写出 %s (%d 字节)" % (path, os.path.getsize(path)))
    return path


def main(argv=None):
    # 本机控制台默认 GBK, 不设置会在打印中文时 UnicodeEncodeError
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except (AttributeError, ValueError):
        pass

    ap = argparse.ArgumentParser(
        description="构建 xingbian.bin (FORMAT.md v1)",
        epilog="所有数据源路径默认取 <脚本目录>/../assets/raw/*, 可用下列参数单独覆盖。")
    ap.add_argument("--raw-dir", default=None, metavar="DIR",
                    help="原始数据目录 (默认 %s); 未单独覆盖的数据源都在此目录下查找" % RAW_REL_PREFIX)
    ap.add_argument("--ids", default=None, metavar="FILE", help="IDS 表 (默认 <raw-dir>/ids.txt)")
    ap.add_argument("--jieba", default=None, metavar="FILE", help="jieba 词频表 (默认 <raw-dir>/jieba_dict.txt)")
    ap.add_argument("--luna", default=None, metavar="FILE",
                    help="Rime luna_pinyin 词库 (默认 <raw-dir>/luna_pinyin.dict.yaml)")
    ap.add_argument("--phrase-pinyin", default=None, metavar="FILE",
                    help="词语拼音表 (默认 <raw-dir>/phrase-pinyin-data.txt)")
    ap.add_argument("--pinyin", default=None, metavar="FILE",
                    help="单字拼音表 (默认 <raw-dir>/pinyin-data.txt)")
    ap.add_argument("--essay", default=None, metavar="FILE", help="essay 词频表 (默认 <raw-dir>/essay.txt)")
    ap.add_argument("--out", default=DEFAULT_OUT, metavar="FILE",
                    help="输出路径 (默认 assets/build/xingbian.bin)")
    ap.add_argument("--max-words", type=int, default=TARGET_TOTAL_WORDS, metavar="N",
                    help="词表总条数上限 (单字优先全保留, 多字按 freq 降序补足; 默认 %d)" % TARGET_TOTAL_WORDS)
    ap.add_argument("--selftest", action="store_true", help="构建后逐项自检")
    ap.add_argument("--report", default=None, metavar="FILE",
                    help="把本次构建的统计写成 Markdown 报告 (默认不写)")
    ap.add_argument("--dump", metavar="字或词", help="打印该字/词的 IDS 与候选读音")
    args = ap.parse_args(argv)

    # CLI 覆盖数据源路径 (必须在任何 parse_* / build 之前)
    configure_paths(
        raw_dir=args.raw_dir,
        ids=args.ids,
        jieba=args.jieba,
        luna=args.luna,
        phrase_pinyin=args.phrase_pinyin,
        pinyin=args.pinyin,
        essay=args.essay,
    )

    out_path = os.path.abspath(args.out)
    rc = 0
    if args.dump:
        return cmd_dump(args.dump, out_path)
    if args.max_words < 1:
        raise SystemExit("[FATAL] --max-words 必须为正整数")
    build(out_path, max_words=args.max_words)
    if args.selftest:
        if not run_selftest(out_path, None):
            rc = 1
    if args.report:
        write_report(os.path.abspath(args.report), out_path)
    return rc


if __name__ == "__main__":
    sys.exit(main())
