#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""下载「形变输入法」所需的基础语料/字表。

网络事实（本机实测）:
  raw.githubusercontent.com  超时  -> 改走 cdn.jsdelivr.net
  api.github.com / unicode.org / chise.org  可用
"""
import hashlib
import io
import json
import os
import sys
import time
import urllib.request
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAW = os.path.join(ROOT, "assets", "raw")
os.makedirs(RAW, exist_ok=True)

# key -> (url, filename, 说明)
SOURCES = {
    "ids": (
        "https://cdn.jsdelivr.net/gh/cjkvi/cjkvi-ids@master/ids.txt",
        "ids.txt",
        "CJKVI/CHISE 表意文字描述序列 (IDS)，拆字核心数据",
    ),
    "unihan": (
        "https://www.unicode.org/Public/UCD/latest/ucd/Unihan.zip",
        "Unihan.zip",
        "Unihan 数据库，含 kZVariant/kSemanticVariant 等异体关系",
    ),
    "luna": (
        "https://cdn.jsdelivr.net/gh/rime/rime-luna-pinyin@master/luna_pinyin.dict.yaml",
        "luna_pinyin.dict.yaml",
        "Rime 明月拼音字典，单字/词语读音",
    ),
    "essay": (
        "https://cdn.jsdelivr.net/gh/rime/rime-essay@master/essay.txt",
        "essay.txt",
        "Rime 八股文词频表，用于候选排序",
    ),
    "pinyin_data": (
        "https://cdn.jsdelivr.net/gh/mozillazg/pinyin-data@master/pinyin.txt",
        "pinyin-data.txt",
        "逐字拼音（含多音字），用于建单字读音表",
    ),
    "phrase_pinyin": (
        "https://cdn.jsdelivr.net/gh/mozillazg/phrase-pinyin-data@master/pinyin.txt",
        "phrase-pinyin-data.txt",
        "词组拼音，用于建词语读音表",
    ),
    "jieba": (
        "https://cdn.jsdelivr.net/gh/fxsjy/jieba@master/jieba/dict.txt",
        "jieba_dict.txt",
        "结巴分词词典（词+词频），用于建词库",
    ),
    "opencc_st": (
        "https://cdn.jsdelivr.net/gh/BYVoid/OpenCC@master/data/dictionary/STCharacters.txt",
        "opencc_STCharacters.txt",
        "简->繁 单字映射",
    ),
    "opencc_ts": (
        "https://cdn.jsdelivr.net/gh/BYVoid/OpenCC@master/data/dictionary/TSCharacters.txt",
        "opencc_TSCharacters.txt",
        "繁->简 单字映射",
    ),
}

PREVIEW_LINES = {
    "ids.txt": (8, 40),
    "luna_pinyin.dict.yaml": (60, 8),
    "essay.txt": (5, 30),
    "pinyin-data.txt": (5, 40),
    "phrase-pinyin-data.txt": (5, 40),
    "jieba_dict.txt": (5, 40),
    "opencc_STCharacters.txt": (12, 15),
    "opencc_TSCharacters.txt": (12, 15),
}


def fetch(url, retries=4, timeout=60):
    last = None
    for i in range(retries):
        try:
            req = urllib.request.Request(
                url, headers={"User-Agent": "Mozilla/5.0 xingbian-ime/0.1"}
            )
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except Exception as e:  # noqa: BLE001
            last = e
            time.sleep(1.5 * (i + 1))
    raise RuntimeError(f"download failed: {url}: {type(last).__name__}: {last}")


def main():
    manifest = {}
    if os.path.exists(os.path.join(RAW, "manifest.json")):
        with open(os.path.join(RAW, "manifest.json"), "r", encoding="utf-8") as f:
            manifest = json.load(f)

    for key, (url, fname, desc) in SOURCES.items():
        dest = os.path.join(RAW, fname)
        if os.path.exists(dest) and os.path.getsize(dest) > 0:
            print(f"[skip] {key:14} {fname} ({os.path.getsize(dest)} bytes)")
        else:
            t0 = time.time()
            try:
                data = fetch(url)
            except Exception as e:  # noqa: BLE001
                print(f"[FAIL] {key:14} {e}")
                continue
            with open(dest, "wb") as f:
                f.write(data)
            print(
                f"[ ok ] {key:14} {fname} {len(data)} bytes "
                f"{time.time() - t0:.1f}s"
            )
        manifest[key] = {
            "url": url,
            "file": fname,
            "desc": desc,
            "size": os.path.getsize(dest),
            "sha256": hashlib.sha256(open(dest, "rb").read()).hexdigest(),
        }

    # 解压 Unihan
    zpath = os.path.join(RAW, "Unihan.zip")
    if os.path.exists(zpath):
        with zipfile.ZipFile(zpath) as z:
            names = z.namelist()
            for n in names:
                if n.endswith(".txt"):
                    out = os.path.join(RAW, "unihan_" + n)
                    if not os.path.exists(out):
                        with z.open(n) as src, open(out, "wb") as dst:
                            dst.write(src.read())
        print(f"[unzip] Unihan -> {len([n for n in names if n.endswith('.txt')])} txt")

    with open(os.path.join(RAW, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)

    # 预览，用于设计解析器
    print("\n" + "=" * 70)
    print("PREVIEW")
    print("=" * 70)
    for fname, (skip, take) in PREVIEW_LINES.items():
        p = os.path.join(RAW, fname)
        if not os.path.exists(p):
            continue
        with open(p, "r", encoding="utf-8", errors="replace") as f:
            lines = []
            for i, line in enumerate(f):
                if i >= skip + take:
                    break
                if i >= skip:
                    lines.append(line.rstrip("\n")[:110])
        print(f"\n--- {fname} ---")
        for line in lines:
            print("   " + line)

    print("\nDONE")


if __name__ == "__main__":
    sys.exit(main())
