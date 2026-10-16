# -*- coding: utf-8 -*-
import io

# luna: check which probe chars are present as single-char entries
want = set(u'丨丿乚亻氵忄扌钅纟饣讠刂卩阝彳宀冖冫辶廴攵夂灬疒衤礻丬尢屮彐彑巛儿二三个女人大天上下回只员真类米具十日一木火口八'
           u'你妈好测亭光当尘尖灰条茶英黄桌省直空学宝完点烈然煎熊燕总单军写农冠冷准况净凉'
           u'朩龰𤴓𧘇⺌龸㝴夬隹𫠣吅玉')
luna_chars = set()
with io.open(r'D:\Documents\deepseek-harness\default-workspace\xingbian-ime\assets\raw\luna_pinyin.dict.yaml', encoding='utf-8') as f:
    in_body = False
    for line in f:
        if line.startswith('...'):
            in_body = True
            continue
        if not in_body:
            continue
        line = line.rstrip('\n')
        if not line or line.startswith('#'):
            continue
        parts = line.split('\t')
        if parts and parts[0]:
            for ch in parts[0]:
                luna_chars.add(ch)

jieba_chars = {}
with io.open(r'D:\Documents\deepseek-harness\default-workspace\xingbian-ime\assets\raw\jieba_dict.txt', encoding='utf-8') as f:
    for line in f:
        parts = line.rstrip('\n').split(' ')
        if len(parts) >= 2:
            try:
                freq = int(parts[1])
            except Exception:
                continue
            for ch in parts[0]:
                jieba_chars[ch] = jieba_chars.get(ch, 0) + freq

out = io.open(r'D:\Documents\deepseek-harness\default-workspace\xingbian-ime\scratch\_probe4.txt', 'w', encoding='utf-8')
for ch in sorted(want):
    out.write(u'%s luna=%s jieba_freq=%d\n' % (ch, ch in luna_chars, jieba_chars.get(ch, 0)))
out.close()
print('done')
