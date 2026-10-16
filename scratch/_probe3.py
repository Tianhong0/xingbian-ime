# -*- coding: utf-8 -*-
import io
want = set(u'光当尘尖灰类条茶英黄桌省直真空学宝完统点烈然煎熊燕总单军写农冠冷准凑决冲况净凉冯习也飞长四画兄弟党 delivered')
want = set(u'光当尘尖灰类条茶英黄桌省直真空学宝完统点烈然煎熊燕总单军写农冠冷准凑决冲况净凉冯习也飞长四画兄弟党')
out = io.open(r'D:\Documents\deepseek-harness\default-workspace\xingbian-ime\scratch\_ids_probe3.txt', 'w', encoding='utf-8')
seen = set()
with io.open(r'D:\Documents\deepseek-harness\default-workspace\xingbian-ime\assets\raw\ids.txt', encoding='utf-8') as f:
    for line in f:
        parts = line.rstrip('\n').split('\t')
        if len(parts) >= 3 and parts[1] in want and parts[1] not in seen:
            seen.add(parts[1])
            out.write(parts[1] + ' | ' + parts[2] + '\n')
out.close()
print('done', len(seen))
