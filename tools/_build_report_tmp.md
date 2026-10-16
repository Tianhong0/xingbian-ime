# xingbian.bin 构建报告

> 本文件由 `tools/build_dict.py --report` 生成，数字全部来自**回读二进制产物**。

## 产物

| 指标 | 值 |
|---|---|
| 路径 | `D:\Documents\deepseek-harness\default-workspace\xingbian-ime\assets\build\xingbian.bin` |
| 大小 | 4373242 字节 (4.171 MB) |
| sha256 | `c0d073a616c6d69dbe31cb76b5a5490c557e92dae2c726a04436ebe5defca658` |
| magic / version | `XBIM` / 1 |

## 各块条数 (回读值)

| 块 | 条数 | payload 字节 |
|---|---|---|
| 1 音节表 | 429 | 1808 |
| 2 词表 | 200000 (单字 52294 / 多字 147706) | 3418546 |
| 3 IDS 表 | 88480 | 945752 |
| 4 Meta | 1 (JSON) | 7008 |

## 块目录 (回读值)

```
blockId=1 reserved=0 offset=128        length=1808       end=1936
blockId=2 reserved=0 offset=1936       length=3418546    end=3420482
blockId=3 reserved=0 offset=3420482    length=945752     end=4366234
blockId=4 reserved=0 offset=4366234    length=7008       end=4373242
```

## 数据源实测 sha256

| 源 | sha256 |
|---|---|
| `essay.txt` | `3e8512ebaa6657e35961b1f5762060717c647e3bf471d5e3b86e783b4e934ffb` |
| `ids.txt` | `bfc70a8c09f9f5616ebf0543bd6681e67314e9f7ae2307e5ae8c6f15bdc5c6a6` |
| `jieba_dict.txt` | `7197c3211ddd98962b036cdf40324d1ea2bfaa12bd028e68faa70111a88e12a8` |
| `luna_pinyin.dict.yaml` | `75bcf6eb3ff62b129882ed89cc22b2d80a5347aa72bcfa2ccc839bac298e7314` |
| `opencc_STCharacters.txt` | `a0ca1601c70648cf48b33c3c6210ccbecc5c7eead4b4c3daf76587ba2c03582b` |
| `opencc_TSCharacters.txt` | `9ff46a7d30e5765375eb13d33f2b03a34d298913caf2b120380679f33ae1642d` |
| `phrase-pinyin-data.txt` | `dcc769607c220b312fea3e71cb63421298b4b891b1f7356a95ab58f2c96fff81` |
| `pinyin-data.txt` | `621f8ca9eff8519f47e2b17b564fd318161e13bca07eea8c8e04993cd5d3b52e` |
