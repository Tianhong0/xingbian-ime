# 端到端验证 harness 结果（由程序自动生成）

生成时间：2026-10-16 18:19:54

本文件的用例观测值由 `E2E.exe` 运行时直接写出，未经人工改写。
（分析结论、RequestLock 双模型对比、ITextStoreACP 方法序核对证据见文件末尾的手写章节。）

## 运行环境

```
================ Xingbian IME 端到端 harness ================
```

## 汇总

| 用例 | 结论 | 判定 |
| --- | --- | --- |
| 用例6 空缓冲区（ActivateEx 之后立即）SPACE / RETURN / BACK 全部 eaten=false | 三个功能键都没被吞 | **PASS** |
| 用例1 主链路 n i m a + 空格 → 假存储文本 == "亻尔 女马" | 文本精确匹配 | **PASS** |
| 用例2 四个字母 n i m a 每个 OnTestKeyDown 都返回 eaten=true | 四个字母全部被吞 | **PASS** |
| 用例3 候选排序：nima+1 应等于"亻尔 女马"，nima+2 应不同 | 1号候选=匹配 ; 2号候选与1号候选不同 | **PASS** |
| 用例4 Esc 取消：nima+Esc 后存储仍为空，且随后空格不被吞 | 存储为空=True ; 空格不被吞=True | **PASS** |
| 用例5 Backspace：nima+BS×2 → 空格上屏应为"亻尔"；n+BS 后空格不被吞 | nima+BS×2+空格="亻尔"(匹配预期) ; n+BS 后空格不被吞=True | **PASS** |
| 用例7 Ctrl / Alt 按下时 N 必须 eaten=false | 基线被吞=True ; Ctrl 不吞（SetKeyboardState） ; Alt 不吞（SetKeyboardState） | **PASS** |
| 用例8 单击 Shift（OnTestKeyUp→OnKeyUp）切换中/英模式 | 中文=True → 切换后英文=True → 再切回中文=True | **PASS** |
| 用例9 候选浮窗 CandidatePanel 全程参与不崩 + 截屏 panel.png | 面板正常，已截屏 | **PASS** |
| 用例10 OnKeyDown(pic=null) / OnTestKeyDown(pic=null) 必须返回 S_OK 且不抛异常 | 无异常逃逸，hr 均为 S_OK (0x00000000 / 0x00000000) | **PASS** |
| 实验A RequestLock 模型 A（同步授权：RequestLock 内回调 OnLockGranted） | 链路跑通，文本正确 | **PASS** |
| 实验B RequestLock 模型 B（延迟授权：*phrSession=TS_S_ASYNC，宿主稍后自己回调 OnLockGranted） | 如预期：模型 B 无法把文本写进存储 | **EXPECTED-FAIL** |
| 附加 ITfTextInputProcessor::Activate 入口（而非 ActivateEx） | Activate 入口同样可用 | **PASS** |

PASS=12 FAIL=0 SKIP=0 ；另有对照实验 1 条，按设计预期就是失败（EXPECTED-FAIL），不计入 FAIL。

## 逐条用例的实际观测值

### 用例6 — 空缓冲区（ActivateEx 之后立即）SPACE / RETURN / BACK 全部 eaten=false

判定：PASS — 三个功能键都没被吞

```
注意：此刻 buffer 必然为空（ActivateEx 内部 ResetInput），且尚未敲过任何字母。
VK 0x20 (VK_SPACE(0x20)) : OnTestKeyDown eaten=False hr=0x00000000 ; OnKeyDown 被调用=False
VK 0x0D (VK_RETURN(0x0D)) : OnTestKeyDown eaten=False hr=0x00000000 ; OnKeyDown 被调用=False
VK 0x08 (VK_BACK(0x08)) : OnTestKeyDown eaten=False hr=0x00000000 ; OnKeyDown 被调用=False
store 文本 = "" (len=0, 码点=)
```

### 用例1 — 主链路 n i m a + 空格 → 假存储文本 == "亻尔 女马"

判定：PASS — 文本精确匹配

```
RequestLock 模型 = SyncGrant
CreateContext 返回的 ecTextStore=1
按键 'N' : OnTestKeyDown eaten=True hr=0x00000000 ; OnKeyDown 被调用=True eaten=True hr=0x00000000
按键 'I' : OnTestKeyDown eaten=True hr=0x00000000 ; OnKeyDown 被调用=True eaten=True hr=0x00000000
按键 'M' : OnTestKeyDown eaten=True hr=0x00000000 ; OnKeyDown 被调用=True eaten=True hr=0x00000000
按键 'A' : OnTestKeyDown eaten=True hr=0x00000000 ; OnKeyDown 被调用=True eaten=True hr=0x00000000
敲完 nima 后 buffer 状态（间接）：敲空格是否被吞将说明 buffer 非空
按键 SPACE : OnTestKeyDown eaten=True ; OnKeyDown 被调用=True eaten=True hr=0x00000000
★ 假文本存储最终文本 = "亻尔 女马" (len=5, 码点=U+4EBB U+5C14 U+0020 U+5973 U+9A6C)
InsertTextAtSelection 被调用次数 = 1
RequestLock 被调用次数 = 1 , OnLockGranted 被调用次数 = 1
最后一次 RequestLock 的 flags = 0x7
RequestEditSession 之后 store 文本 = "亻尔 女马" (len=5, 码点=U+4EBB U+5C14 U+0020 U+5973 U+9A6C)
---- 假文本存储事件日志 ----
  [rig] new TextService() + ActivateEx -> hr=0x00000000（本进程累计实例数 3）
  [rig] CreateContext 成功 ecTextStore=1
  [rig] Push hr=0x00000000
  [prepare] 修饰键清理：清前 Ctrl=0x0000 Alt=0x0000 Shift=0x0000（干净） → 清后 Ctrl=0x0000 Alt=0x0000
  [prepare] buffer 已清空（退格 1 次内收敛）
  [prepare] 当前敲 N 被吞（=中文模式）= True
  [key] 'N' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'I' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'M' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'A' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_SPACE(0x20) testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
```

### 用例2 — 四个字母 n i m a 每个 OnTestKeyDown 都返回 eaten=true

判定：PASS — 四个字母全部被吞

```
VK 0x4E ('N') OnTestKeyDown eaten=True hr=0x00000000 ; OnKeyDown 也被调用=True 其 eaten=True
VK 0x49 ('I') OnTestKeyDown eaten=True hr=0x00000000 ; OnKeyDown 也被调用=True 其 eaten=True
VK 0x4D ('M') OnTestKeyDown eaten=True hr=0x00000000 ; OnKeyDown 也被调用=True 其 eaten=True
VK 0x41 ('A') OnTestKeyDown eaten=True hr=0x00000000 ; OnKeyDown 也被调用=True 其 eaten=True
---- 假文本存储事件日志 ----
  [rig] new TextService() + ActivateEx -> hr=0x00000000（本进程累计实例数 4）
  [rig] CreateContext 成功 ecTextStore=1
  [rig] Push hr=0x00000000
  [prepare] 修饰键清理：清前 Ctrl=0x0000 Alt=0x0000 Shift=0x0000（干净） → 清后 Ctrl=0x0000 Alt=0x0000
  [prepare] buffer 已清空（退格 1 次内收敛）
  [prepare] 当前敲 N 被吞（=中文模式）= True
  [key] 'N' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'I' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'M' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'A' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
```

### 用例3 — 候选排序：nima+1 应等于"亻尔 女马"，nima+2 应不同

判定：PASS — 1号候选=匹配 ; 2号候选与1号候选不同

```
数字键 1 : OnTestKeyDown eaten=True ; OnKeyDown 被调用=True eaten=True hr=0x00000000
数字键 1 : InsertTextAtSelection 次数=1 , store 文本="亻尔 女马" (len=5, 码点=U+4EBB U+5C14 U+0020 U+5973 U+9A6C)
数字键 2 : OnTestKeyDown eaten=True ; OnKeyDown 被调用=True eaten=True hr=0x00000000
数字键 2 : InsertTextAtSelection 次数=1 , store 文本="尸匕 王马" (len=5, 码点=U+5C38 U+5315 U+0020 U+738B U+9A6C)
nima + 数字键1 → "亻尔 女马" (len=5, 码点=U+4EBB U+5C14 U+0020 U+5973 U+9A6C)
nima + 数字键2 → "尸匕 王马" (len=5, 码点=U+5C38 U+5315 U+0020 U+738B U+9A6C)
两次结果相同? False
```

### 用例4 — Esc 取消：nima+Esc 后存储仍为空，且随后空格不被吞

判定：PASS — 存储为空=True ; 空格不被吞=True

```
ESC : OnTestKeyDown eaten=True ; OnKeyDown 被调用=True eaten=True hr=0x00000000
ESC 之后 store 文本 = "" (len=0, 码点=)
ESC 之后 InsertTextAtSelection 次数 = 0
ESC 之后 SPACE : OnTestKeyDown eaten=False ; OnKeyDown 被调用=False
---- 假文本存储事件日志 ----
  [rig] new TextService() + ActivateEx -> hr=0x00000000（本进程累计实例数 7）
  [rig] CreateContext 成功 ecTextStore=1
  [rig] Push hr=0x00000000
  [prepare] 修饰键清理：清前 Ctrl=0x0000 Alt=0x0000 Shift=0x0000（干净） → 清后 Ctrl=0x0000 Alt=0x0000
  [prepare] buffer 已清空（退格 1 次内收敛）
  [prepare] 当前敲 N 被吞（=中文模式）= True
  [key] 'N' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'I' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'M' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'A' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_ESCAPE(0x1B) testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_SPACE(0x20) testEaten=False hrTest=0x00000000 downCalled=False
```

### 用例5 — Backspace：nima+BS×2 → 空格上屏应为"亻尔"；n+BS 后空格不被吞

判定：PASS — nima+BS×2+空格="亻尔"(匹配预期) ; n+BS 后空格不被吞=True

```
第 1 次 BS : OnTestKeyDown eaten=True , OnKeyDown 被调用=True eaten=True
第 2 次 BS : OnTestKeyDown eaten=True , OnKeyDown 被调用=True eaten=True
两次 BS 之后 SPACE : OnTestKeyDown eaten=True , OnKeyDown 被调用=True
★ nima+BS×2+空格 → 实际上屏文本 = "亻尔" (len=2, 码点=U+4EBB U+5C14) （参考：nim 的首候选亦为「你妈」；再退一次得到 ni）
5b: 敲 n 后 BS : OnTestKeyDown eaten=True , OnKeyDown 被调用=True eaten=True
5b: buffer 清空后 SPACE : OnTestKeyDown eaten=False , OnKeyDown 被调用=False
5b: store 文本 = "" (len=0, 码点=)
---- 假文本存储事件日志 ----
  [rig] new TextService() + ActivateEx -> hr=0x00000000（本进程累计实例数 8）
  [rig] CreateContext 成功 ecTextStore=1
  [rig] Push hr=0x00000000
  [prepare] 修饰键清理：清前 Ctrl=0x0000 Alt=0x0000 Shift=0x0000（干净） → 清后 Ctrl=0x0000 Alt=0x0000
  [prepare] buffer 已清空（退格 1 次内收敛）
  [prepare] 当前敲 N 被吞（=中文模式）= True
  [key] 'N' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'I' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'M' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'A' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_BACK(0x08) testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_BACK(0x08) testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_SPACE(0x20) testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
---- 5b 事件日志 ----
  [rig] new TextService() + ActivateEx -> hr=0x00000000（本进程累计实例数 9）
  [rig] CreateContext 成功 ecTextStore=1
  [rig] Push hr=0x00000000
  [prepare] 修饰键清理：清前 Ctrl=0x0000 Alt=0x0000 Shift=0x0000（干净） → 清后 Ctrl=0x0000 Alt=0x0000
  [prepare] buffer 已清空（退格 1 次内收敛）
  [prepare] 当前敲 N 被吞（=中文模式）= True
  [key] 'N' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_BACK(0x08) testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_SPACE(0x20) testEaten=False hrTest=0x00000000 downCalled=False
```

### 用例7 — Ctrl / Alt 按下时 N 必须 eaten=false

判定：PASS — 基线被吞=True ; Ctrl 不吞（SetKeyboardState） ; Alt 不吞（SetKeyboardState）

```
[环境基线] SendInput 松开 6 个修饰键 → 返回 6 ; 修饰键清理：清前 Ctrl=0x0000 Alt=0x0000 Shift=0x0000（干净） → 清后 Ctrl=0x0000 Alt=0x0000
基线（无修饰键）敲 N : OnTestKeyDown eaten=True（预期 true）
SendInput 按下 Ctrl : 返回=1 , GetKeyState=0x0000 高位=False
SetKeyboardState 置 Ctrl : SetKeyboardState=True , GetKeyState=0xFF80 高位=True
  → 按住 Ctrl 敲 N : OnTestKeyDown eaten=False（预期 false）
  松开后 GetKeyState=0x0000
SendInput 按下 Alt : 返回=1 , GetKeyState=0x0000 高位=False
SetKeyboardState 置 Alt : SetKeyboardState=True , GetKeyState=0xFF80 高位=True
  → 按住 Alt 敲 N : OnTestKeyDown eaten=False（预期 false）
  松开后 GetKeyState=0x0000
[收尾] SendInput 松开 6 个修饰键 → 返回 6 ; 修饰键清理：清前 Ctrl=0x0000 Alt=0x0000 Shift=0x0000（干净） → 清后 Ctrl=0x0000 Alt=0x0000
```

### 用例8 — 单击 Shift（OnTestKeyUp→OnKeyUp）切换中/英模式

判定：PASS — 中文=True → 切换后英文=True → 再切回中文=True

```
8a 切换前：敲 N 被吞（=中文模式）= True（全新实例，_chineseMode 初值 true、_shiftChord=false）
8a 第 1 对 OnTestKeyUp(VK_SHIFT)+OnKeyUp(VK_SHIFT) 后：敲 N 被吞 = False（预期 false，即已切到英文）
8a 第 2 对后：敲 N 被吞 = True（预期 true，即切回中文）
8a 实际使用时序 = OnTestKeyUp(VK_SHIFT) → OnKeyUp(VK_SHIFT)，两者之间不调用任何其它入口
8a 结论：中文=True → 英文=True → 中文=True
---- 假文本存储事件日志 ----
  [rig] new TextService() + ActivateEx -> hr=0x00000000（本进程累计实例数 11）
  [rig] CreateContext 成功 ecTextStore=1
  [rig] Push hr=0x00000000
  [prepare] 修饰键清理：清前 Ctrl=0x0000 Alt=0x0000 Shift=0x0000（干净） → 清后 Ctrl=0x0000 Alt=0x0000
  [prepare] buffer 已清空（退格 1 次内收敛）
  [prepare] 当前敲 N 被吞（=中文模式）= True
  [shift 第1对] OnTestKeyUp(VK_SHIFT) hr=0x00000000 eaten=False ; OnKeyUp(VK_SHIFT) hr=0x00000000 eaten=False
  [shift 第2对] OnTestKeyUp(VK_SHIFT) hr=0x00000000 eaten=False ; OnKeyUp(VK_SHIFT) hr=0x00000000 eaten=False
8b 起点：中文模式 = True
8b 敲 n（eaten=True）再退格（eaten=True）：缓冲区已被退格清空，但 OnKeyDown 里 _shiftChord 已被置 true 且无人清除
8b 第 1 对 Shift 后：敲 N 被吞 = True（若仍为 true，说明这一次单击被闩锁吞掉了）
8b 第 2 对 Shift 后：敲 N 被吞 = False（预期 false，第 2 次才切换）
8b 判定：复现了『退格清空缓冲区后，第一次 Shift 单击无效』
---- 假文本存储事件日志 ----
  [rig] new TextService() + ActivateEx -> hr=0x00000000（本进程累计实例数 12）
  [rig] CreateContext 成功 ecTextStore=1
  [rig] Push hr=0x00000000
  [key] 'N' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_BACK(0x08) testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [shift 8b-第1对] OnTestKeyUp(VK_SHIFT) hr=0x00000000 eaten=False ; OnKeyUp(VK_SHIFT) hr=0x00000000 eaten=False
  [shift 8b-第2对] OnTestKeyUp(VK_SHIFT) hr=0x00000000 eaten=False ; OnKeyUp(VK_SHIFT) hr=0x00000000 eaten=False
```

### 用例9 — 候选浮窗 CandidatePanel 全程参与不崩 + 截屏 panel.png

判定：PASS — 面板正常，已截屏

```
FindWindowW("XingbianIME.CandidatePanel.Window") = 0x96097C
面板 EX 样式 = 0x08000088 (TOOLWINDOW=True, TOPMOST=True, NOACTIVATE=True)
class="XingbianIME.CandidatePanel.Window" title="Xingbian IME Candidate Panel"
IsWindowVisible=True
GetWindowRect = (4,7)-(167,105) 尺寸 163x98
强制显示后 IsWindowVisible=True
PrintWindow -> True , 非黑像素=2662
BitBlt(屏幕 DC) -> True , 非黑像素=2662
已保存 PNG: D:\Documents\deepseek-harness\default-workspace\xingbian-ime\scratch\e2e\panel.png (2172 字节)
PNG 尺寸 = 163x98
截图结果: 已保存 D:\Documents\deepseek-harness\default-workspace\xingbian-ime\scratch\e2e\panel.png
进程仍存活 = true（若本用例返回则说明没崩）
ESC 收起面板 : OnTestKeyDown eaten=True
ESC 之后 IsWindowVisible=False
---- 假文本存储事件日志 ----
  [rig] new TextService() + ActivateEx -> hr=0x00000000（本进程累计实例数 13）
  [rig] CreateContext 成功 ecTextStore=1
  [rig] Push hr=0x00000000
  [prepare] 修饰键清理：清前 Ctrl=0x0000 Alt=0x0000 Shift=0x0000（干净） → 清后 Ctrl=0x0000 Alt=0x0000
  [prepare] buffer 已清空（退格 1 次内收敛）
  [prepare] 当前敲 N 被吞（=中文模式）= True
  [key] 'N' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'I' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'M' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'A' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_ESCAPE(0x1B) testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
```

### 用例10 — OnKeyDown(pic=null) / OnTestKeyDown(pic=null) 必须返回 S_OK 且不抛异常

判定：PASS — 无异常逃逸，hr 均为 S_OK (0x00000000 / 0x00000000)

```
本用例使用独立 TextService 实例（第 14 个）
OnTestKeyDown(null, VK_N) -> hr=0x00000000 eaten=True
OnKeyDown(null, VK_N) -> hr=0x00000000 eaten=True
OnKeyDown(null, VK_SPACE) -> hr=0x00000000 eaten=True
```

### 实验A — RequestLock 模型 A（同步授权：RequestLock 内回调 OnLockGranted）

判定：PASS — 链路跑通，文本正确

```
RequestLock 调用次数 = 1
OnLockGranted 调用次数 = 1
InsertTextAtSelection 调用次数 = 1
最后一次锁标志 = 0x7 (TS_LF_SYNC=True, TS_LF_READWRITE=True)
★ store 文本 = "亻尔 女马" (len=5, 码点=U+4EBB U+5C14 U+0020 U+5973 U+9A6C)
---- 假文本存储事件日志 ----
  [rig] new TextService() + ActivateEx -> hr=0x00000000（本进程累计实例数 16）
  [rig] CreateContext 成功 ecTextStore=1
  [rig] Push hr=0x00000000
  [prepare] 修饰键清理：清前 Ctrl=0x0000 Alt=0x0000 Shift=0x0000（干净） → 清后 Ctrl=0x0000 Alt=0x0000
  [prepare] buffer 已清空（退格 1 次内收敛）
  [prepare] 当前敲 N 被吞（=中文模式）= True
  [key] 'N' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'I' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'M' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'A' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_SPACE(0x20) testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
```

### 实验B — RequestLock 模型 B（延迟授权：*phrSession=TS_S_ASYNC，宿主稍后自己回调 OnLockGranted）

判定：EXPECTED-FAIL — 如预期：模型 B 无法把文本写进存储

```
SPACE : OnTestKeyDown eaten=True , OnKeyDown 被调用=True
RequestEditSession（在 OnKeyDown 内部）返回后：
  RequestLock 调用次数 = 2
  OnLockGranted 调用次数 = 0（模型 B 下宿主还没授权，预期 0）
  HasDeferredLock = True
  InsertTextAtSelection 次数 = 0
  store 文本 = "" (len=0, 码点=)
  面板是否仍显示 = 窗口存在 , IsWindowVisible=True
宿主补调 RunDeferredLock() -> True
  OnLockGranted 调用次数 = 1
  InsertTextAtSelection 次数 = 0
  ★ 补授权之后 store 文本 = "" (len=0, 码点=)

结论：模型 B 下文本【没有】进到存储里。
原因（读 TextService.cs 的 InsertIntoDocument 得到）：RequestEditSession 一返回，它就把 _pendingInsert/_pendingContext 清成了 null，_pendingInsertHr 仍是 E_FAIL，于是 CommitSelected 认为提交失败；等宿主稍后补授权时 DoEditSession 看到的 _pendingInsert 已经是 null，直接返回 S_OK 什么都没写。也就是说 TextService 假设 RequestEditSession 一定是“同会话内同步完成”的，这对异步锁（TF_ES_ASYNC / TS_S_ASYNC）是错的——只是它自己从不请求异步锁，所以线上不会触发。
---- 假文本存储事件日志 ----
  [rig] new TextService() + ActivateEx -> hr=0x00000000（本进程累计实例数 17）
  [rig] CreateContext 成功 ecTextStore=1
  [rig] Push hr=0x00000000
  [prepare] 修饰键清理：清前 Ctrl=0x0000 Alt=0x0000 Shift=0x0000（干净） → 清后 Ctrl=0x0000 Alt=0x0000
  [prepare] buffer 已清空（退格 1 次内收敛）
  [prepare] 当前敲 N 被吞（=中文模式）= True
  [key] 'N' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'I' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'M' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] 'A' testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
  [key] VK_SPACE(0x20) testEaten=True hrTest=0x00000000 downCalled=True downEaten=True hrDown=0x00000000
```

### 附加 — ITfTextInputProcessor::Activate 入口（而非 ActivateEx）

判定：PASS — Activate 入口同样可用

```
Activate 之前先探一下：OnTestKeyDown(VK_N) eaten=False（预期 false，_active 还是 false）
Activate(ptim, tid) -> hr=0x00000000
随后 OnTestKeyDown(VK_N) -> hr=0x00000000 eaten=True（预期 true，说明 Activate 也把 _active 置上了）
nima+空格 → store 文本 = "亻尔 女马" (len=5, 码点=U+4EBB U+5C14 U+0020 U+5973 U+9A6C)
```

## 完整控制台输出

见同目录 `run-output.txt`。

<!-- HANDWRITTEN-SECTIONS-BELOW -->







## ① 测试对象与构建

被测程序集由 harness 通过 `ProjectReference` 直接引用源码工程，编译的是磁盘上的真实 `src\Xingbian.Tsf`，不是任何副本：

```
& "C:\Program Files\dotnet\dotnet.exe" build "D:\Documents\deepseek-harness\default-workspace\xingbian-ime\scratch\e2e\E2E.csproj" -c Release -v m -nologo -m:1
  Xingbian.Core -> ...\src\Xingbian.Core\bin\Release\Xingbian.Core.dll
  Xingbian.Tsf  -> ...\src\Xingbian.Tsf\bin\Release\Xingbian.Tsf.dll
  E2E           -> ...\scratch\e2e\bin\Release\E2E.exe
已成功生成。 0 个警告 0 个错误
```

两个必须记住的构建约束（都是本沙箱实测出来的，不是推测）：

* **必须带 `-m:1`。** 不加时，`E2E.csproj` 里 `_GetProjectReferenceTargetFrameworkProperties` 会派生一个 MSBuild worker 节点去子工程跑 `GetTargetFrameworks`，该 worker 在本沙箱里会静默死亡，表现为自相矛盾的 `生成失败。 0 个警告 0 个错误`，连编译器都不会启动（`-v diag` 里只有 `已完成执行任务"MSBuild"的操作 - 失败。 (任务 ID: 101)`，没有任何 MSB/CS 错误码）。
* **`E2E.csproj` 必须排除 `_stale\**`。** SDK 风格工程的默认 glob 是递归的 `**/*.cs`，会把废弃草稿一起编进来造成类型重复定义。

沙箱限制（决定了整套方案的形态）：进程处于 Low 完整性级别，**任何注册表写入都是 Access Denied**，因此不可能注册 TIP、不可能让 TSF 通过 CLSID 找到这个 TextService。这正是 harness 存在的理由 —— 同一进程内直接 `new TextService()`，把**真实的 `ITfThreadMgr`** 喂给它，再自己实现 `ITextStoreACP` 当假宿主，于是「按键 → 候选 → 上屏」走的是真实 `msctf.dll` 的编辑会话机制。

## ② 逐条用例实测结果

10 条指定用例 **全部 PASS**；另外 3 条附加项（模型 A 复现、模型 B 对照、`Activate` 入口）单独列出。

| # | 用例 | 实测结果（原始值） | 判定 |
| --- | --- | --- | --- |
| 1 | 主链路 `nima`+空格 | 假存储文本 `"亻尔 女马"`，len=5，码点 `U+4EBB U+5C14 U+0020 U+5973 U+9A6C`（精确相等；`InsertTextAtSelection` 被调用 1 次，`RequestLock` 1 次、`OnLockGranted` 1 次，锁标志 `0x7`） | PASS |
| 2 | 四个字母各自被吞 | `N`/`I`/`M`/`A` 四次 `OnTestKeyDown` 均 `eaten=True hr=0x00000000`，且 `OnKeyDown` 也均 `eaten=True` | PASS |
| 3 | 候选排序 | `nima`+`1` → `"亻尔 女马"`（`U+4EBB U+5C14 U+0020 U+5973 U+9A6C`）；`nima`+`2` → `"尸匕 王马"`（`U+5C38 U+5315 U+0020 U+738B U+9A6C`）；两次结果相同=False | PASS |
| 4 | Esc 取消 | Esc 后 `store 文本=""`（len=0）、`InsertTextAtSelection 次数=0`；随后空格 `OnTestKeyDown eaten=False` | PASS |
| 5 | Backspace | `nima`+BS×2 后 buffer=`ni`，空格上屏 → `"亻尔"`（len=2，`U+4EBB U+5C14`，与预期一致）；5b：`n`+BS 后空格 `eaten=False`、store 保持空 | PASS |
| 6 | 空缓冲区不吞功能键 | `VK_SPACE` / `VK_RETURN` / `VK_BACK` 三者 `eaten=False`，`OnKeyDown` 未被调用 | PASS |
| 7 | Ctrl / Alt 不吞 | 基线（无修饰键）敲 `N` `eaten=True`；Ctrl 注入成功后 `eaten=False`；Alt 注入成功后 `eaten=False`（两者均为 `SetKeyboardState` 路径，详见 ⑤） | PASS |
| 8 | Shift 单击切换中/英 | 8a：中文 `True` → 第 1 对 Shift 后 `False` → 第 2 对后 `True`；8b：复现了「退格后第一次 Shift 无效」的闩锁缺陷（详见 ⑥ 缺陷 2） | PASS |
| 9 | 候选浮窗不崩 + 截屏 | `FindWindowW("XingbianIME.CandidatePanel.Window")=0x22091C`；EX 样式 `0x08000088`（TOOLWINDOW/TOPMOST/NOACTIVATE 三位全中）；`IsWindowVisible=True`；`GetWindowRect=(4,7)-(167,105)` 尺寸 163x98；截屏 `panel.png` 2172 字节；Esc 后 `IsWindowVisible=False`；进程存活 | PASS |
| 10 | 异常安全 `pic=null` | `OnTestKeyDown(null,VK_N)→hr=0x00000000`、`OnKeyDown(null,VK_N)→hr=0x00000000`、`OnKeyDown(null,VK_SPACE)→hr=0x00000000`，无异常逃逸 | PASS |

关于用例 5 补一句实测校正：提问里给的备选写法是「`nima`+**一次** Backspace → 结果不应是 `亻尔 女马`，但注意 `nim` 的首候选实测是「你妈」」—— 两句自相矛盾。实测证实**后一句是对的**：`nim` 的首候选确实是「你妈」，所以退一次再空格仍然上屏 `亻尔 女马`；必须退**两次**（得到 `ni`）才会变成 `亻尔`。harness 最终采用的就是退两次的写法。

关于用例 3 的间接验证：私有字段确实读不到，但数字键 1/2 走的是 `SelectByDigit(digit, pic)` → `_selectedIndex = digit-1` → `CommitSelected`，与空格走同一条提交路径，所以「1 号候选 == 空格默认候选、2 号候选 != 1 号候选」足以证明候选列表非空、有序、且选中索引被正确使用。第 2 候选实测为 `尸匕 王马`（对应「尼玛」的 IDS 拆解）。

## ③ 最重要的技术结论：`RequestLock` 该用哪种模型

**结论：模型 A（同步授权）是唯一能让链路跑通的模型 —— `RequestLock` 内部必须同步回调 `ITextStoreACPSink::OnLockGranted`，由 TSF 在 `OnLockGranted` 里调 `ITfEditSession::DoEditSession`。**

两种模型的实测差异（同一条 `nima`+空格 路径，仅切换假存储的 `RequestLock` 行为）：

| 观测项 | 模型 A `SyncGrant` | 模型 B `DeferredGrant` |
| --- | --- | --- |
| `RequestLock` 收到的标志 | `0x7` = `TS_LF_SYNC\|TS_LF_READWRITE` | 同样 `0x7` |
| `RequestLock` 返回的 `*phrSession` | `S_OK` | `TS_S_ASYNC` (`0x00040300`) |
| TSF 是否回调 `OnLockGranted` | **调用 1 次** | **调用 0 次**（宿主补调前） |
| `DoEditSession` 是否执行 | 执行 | 不执行 |
| `InsertTextAtSelection` 被调用次数 | **1** | **0** |
| 假存储最终文本 | **`"亻尔 女马"`** | `""`（空） |
| 宿主事后补调 `RunDeferredLock()` | — | `OnLockGranted` 计到 1 次，但 `InsertTextAtSelection` **仍是 0**，store 仍为 `""` |

因果链（两处独立证据，一处实现、一处规范）：

1. **接口签名本身就排除了另一种解读。** `textstor.h:534-537` 原文：
   ```c
   HRESULT (STDMETHODCALLTYPE *RequestLock)(
       ITextStoreACP *This,
       DWORD dwLockFlags,
       HRESULT *phrSession);
   ```
   参数里**没有** session/`ec` 指针，所以文本存储在 `RequestLock` 里**物理上无法**调用 `DoEditSession`（它既拿不到 `ITfEditSession*`，也拿不到合法的 `ec`）。唯一可能的机制就是：存储回调 sink，由持有 session 的 TSF 自己去调。

2. **TSF 实现侧就是这么写的。** Wine 的 `dlls/msctf/context.c`（`scratch\e2e\wine_context.c`，本任务开工时下载留存）`Context_RequestEditSession` 第 251 行把锁请求转给存储后，`TextStoreACPSink_OnLockGranted`（第 882 行）在第 914 行执行：
   ```c
   hr = ITfEditSession_DoEditSession(This->currentEditSession, ec);
   ```
   而 `ec` 是**在 `OnLockGranted` 内部**刚铸出来的（第 910-912 行 `cookie->lockType = dwLockFlags; ec = generate_Cookie(COOKIE_MAGIC_EDITCOOKIE, cookie);`），此前 `Context_RequestEditSession` 只是把 session QI 存进 `This->currentEditSession`（第 245 行）。同一文件第 893-897 行还说明：`OnLockGranted` 也会被非编辑会话的用途调用（第 866 行 `ITextStoreACP_RequestLock(..., TS_LF_READ, &hrSession)`），此时必须无害返回 —— 假存储的模型 A 实现正是这么做的。

模型 B 失败的第二层原因在**被测代码自身**，值得单独记录：`TextService.InsertIntoDocument`（`TextService.cs:736-756`）在 `RequestEditSession` 返回后立刻把 `_pendingInsert`/`_pendingContext` 清成 `null`（第 751-752 行），而 `_pendingInsertHr` 此时仍是 `E_FAIL`；等宿主稍后补授权时 `DoEditSession`（第 478-493 行）看到的 `_pendingInsert` 已经是 `null`，于是「成功」地什么也不写。也就是说 `TextService` 假设 `RequestEditSession` 一定是**同调用内同步完成**的 —— 对 `TF_ES_ASYNC`/`TS_S_ASYNC` 异步锁这个假设不成立（只是它自己只用 `TF_ES_SYNC`，所以线上不会触发）。这解释了为什么模型 B 即使在宿主补授权之后 store 依然为空。

另外记录一个未深挖的原始观测：模型 B 跑完后 `RequestLock` 被调用了 **2 次**（模型 A 是 1 次），而 `OnLockGranted` 是 0 次。harness 没有进一步埋点区分这第二次是 TSF 在收到 `TS_S_ASYNC` 后的重试、还是通知路径另起的锁请求；这里如实列出数字，不做无证据的归因。

## ④ `ITextStoreACP` 方法序是怎么核对的

`ITextStoreACP` **不在**项目里现有的 `TsfInterop.cs` 中，也不在 `sdk_msctf.h` 里（后者只有 `ITextStoreACPServices`，`scratch\e2e\..\.ref\sdk_msctf.h:6147-6270`）——它属于 `textstor.h` / `textstor.idl`。核对过程是「拿到权威定义 → 逐行读 vtable → 另用一份独立来源交叉验证 → 再用运行时行为反证」：

**第 1 步：取权威定义。** `curl.exe` 在本机完全不可用；PowerShell 的 `Invoke-WebRequest` 访问 cdn.jsdelivr.net 报「基础连接已经关闭」；**Python `urllib` 可以用**。最终取到两份可直接读的原文（都是微软 IDL 内容被再分发，vtable 顺序与 Windows SDK 一致）：

* `scratch\e2e\textstor.h`（31982 B）← `cdn.jsdelivr.net/gh/mingw-w64/mingw-w64@master/mingw-w64-headers/include/textstor.h`
* `scratch\e2e\textstor.idl`（10663 B）← `cdn.jsdelivr.net/gh/reactos/reactos@master/sdk/include/psdk/textstor.idl`

**第 2 步：逐行读 `ITextStoreACPVtbl`。** vtable 结构体在 `textstor.h:508` 打开（`typedef struct ITextStoreACPVtbl {`）、`textstor.h:698` 的 `END_INTERFACE` / `:699` 的 `} ITextStoreACPVtbl;` 关闭。开头原文（`textstor.h:508-537`）：

```c
typedef struct ITextStoreACPVtbl {
    BEGIN_INTERFACE

    /*** IUnknown methods ***/
    HRESULT (STDMETHODCALLTYPE *QueryInterface)(
        ITextStoreACP *This,
        REFIID riid,
        void **ppvObject);

    ULONG (STDMETHODCALLTYPE *AddRef)(
        ITextStoreACP *This);

    ULONG (STDMETHODCALLTYPE *Release)(
        ITextStoreACP *This);

    /*** ITextStoreACP methods ***/
    HRESULT (STDMETHODCALLTYPE *AdviseSink)(
        ITextStoreACP *This,
        REFIID riid,
        IUnknown *punk,
        DWORD dwMask);

    HRESULT (STDMETHODCALLTYPE *UnadviseSink)(
        ITextStoreACP *This,
        IUnknown *punk);

    HRESULT (STDMETHODCALLTYPE *RequestLock)(
        ITextStoreACP *This,
        DWORD dwLockFlags,
        HRESULT *phrSession);

    HRESULT (STDMETHODCALLTYPE *GetStatus)(
        ITextStoreACP *This,
        TS_STATUS *pdcs);

    HRESULT (STDMETHODCALLTYPE *QueryInsert)(
        ITextStoreACP *This,
        LONG acpTestStart,
        LONG acpTestEnd,
        ULONG cch,
        LONG *pacpResultStart,
        LONG *pacpResultEnd);
```

用一条正则 `HRESULT \(STDMETHODCALLTYPE \*(\w+)\)\(` 把该 vtable 段落里的所有槽位按出现顺序抓出来，得到**完整且唯一的 26 项方法序**（行号为 `textstor.h` 内行号）：

```
 0 AdviseSink                           :524      13 InsertTextAtSelection             :611
 1 UnadviseSink                         :530      14 InsertEmbeddedAtSelection         :620
 2 RequestLock                          :534      15 RequestSupportedAttrs             :628
 3 GetStatus                            :539      16 RequestAttrsAtPosition             :634
 4 QueryInsert                          :543      17 RequestAttrsTransitioningAtPosition:641
 5 GetSelection                         :551      18 FindNextAttrTransition             :648
 6 SetSelection                         :558      19 RetrieveRequestedAttrs             :659
 7 GetText                              :563      20 GetEndACP                          :665
 8 SetText                              :575      21 GetActiveView                      :669
 9 GetFormattedText                     :584      22 GetACPFromPoint                    :673
10 GetEmbedded                          :590      23 GetTextExt                         :680
11 QueryInsertEmbedded                  :597      24 GetScreenExt                       :688
12 InsertEmbedded                       :603      25 GetWnd                             :693
```

加上 `:512`/`:517`/`:520` 的三个 IUnknown 槽，共 29 个 vtable 槽位。**`scratch\e2e\Interop.cs:146-229` 里 harness 自己的 `ITextStoreACP` 声明就是严格照这张表写的、共 26 个 `[PreserveSig] int` 方法**，一个不多一个不少、顺序完全一致。

**第 3 步：交叉验证 + 关键签名。** 独立来源 `scratch\e2e\textstor.idl:113-302` 以 `interface ITextStoreACP : IUnknown {` 声明同样的方法序（IDL 声明顺序 == vtable 顺序）。IID 也从同一处取：`textstor.h:505` 的 `__CRT_UUID_DECL(ITextStoreACP, 0x28888fe3, 0xc2a0, 0x483a, 0xa3,0xea, 0x8c,0xb1,0xce,0x51,0xff,0x3d)` = `28888fe3-c2a0-483a-a3ea-8cb1ce51ff3d`，`Interop.cs:143` 的 `[Guid]` 用的就是这个值。最容易和 TSF 侧同名方法搞混的一处也在这里核对过：**文本存储侧的 `InsertTextAtSelection` 是 6 参数**（`textstor.h:611-618`：`dwFlags, pchText, cch, pacpStart, pacpEnd, pChange`），而 TSF 侧的 `ITfInsertAtSelection::InsertTextAtSelection` 是 5 参数、末位是 `ITfRange **ppRange`（`.ref\msctf.idl:1465-1476`）。

**第 4 步：运行时反证（比读头文件更强的证据）。** 如果方法序错一位，TSF 按槽位调用时就会把 `InsertTextAtSelection` 的参数当成别的签名来解释，结果是崩溃或彻底不工作。实测里 TSF 命中了一条**跨进程边界的完整调用链**并且拿到了正确结果：`RequestLock(flags=0x7)` 被调用 → `OnLockGranted` 被回调 → `ITfEditSession::DoEditSession(ec)` 被执行 → `ITfInsertAtSelection::InsertTextAtSelection` → 最终落到假存储的 `InsertTextAtSelection` 且文本精确为 `亻尔 女马`、`GetSelection`/`GetText`/`GetEndACP` 均按预期被调用。这条链同时穿过了本 harness 声明的三个接口（`ITextStoreACP` 26 槽、`ITextStoreACPSink` 8 槽、TSF 侧全部沿用手写 `TsfInterop.cs`），是全序正确的实测证据。顺带确认 `ITextStoreACPSink` 的 8 槽方法序（`textstor.h:244-275`）与我们的声明一致：`OnTextChange / OnSelectionChange / OnLayoutChange / OnStatusChange / OnAttrsChange / OnLockGranted / OnStartEditTransaction / OnEndEditTransaction`。

还给一条结构体布局的核对结论：`TS_SELECTION_ACP` 在 x64 上是 `LONG acpStart; LONG acpEnd; TsActiveSelEnd ase(4B); BOOL fInterimChar(4B)` = 16 字节且**首字段之后紧跟指针**这一点被 `TextService.TrySetTextAtSelection` 的 `const int SelectionSize = 16;` 与 `Marshal.ReadIntPtr(pSelection)` 用法所依赖 —— 该尺寸与 `textstor.idl` 的字段定义吻合（本次运行没走到该回退路径，见 ⑤）。

## ⑤ 我没有验证到的部分

以下条目**没有**被验证，harness 的任何 PASS 都不应被理解为覆盖了它们：

1. **没有验证真实的按键路由。** TIP 未注册（Low 完整性级别下注册表写全部 Access Denied），所以键事件不是从「键盘消息 → `msctf.dll` → `ITfKeyEventSink`」这条路进来的。harness 是**直接调用** `TextService.OnTestKeyDown/OnKeyDown/OnTestKeyUp/OnKeyUp`，但严格按真实 TSF 的调用契约来：`OnTestKeyDown` 返回 `false` 就**不**调 `OnKeyDown`（`Program.cs` 的 `SendKey`）。同理 `ITfKeystrokeMgr::TestKeyDown/KeyDown` 这条路由路径未验证。
2. **没有验证与真实宿主的互操作**（记事本 / Word / 浏览器 / 任何真实 `ITextStoreACP` 实现）。假存储是极简实现，只覆盖 TSF 会让它走的那些调用。
3. **用例 7 的「真实物理修饰键」路径未验证。** 实测 `SendInput` 在本会话里注入不进去：`SendInput 按下 Ctrl : 返回=1 , GetKeyState=0x0000 高位=False`（Alt 同样）。因此改用 `SetKeyboardState` 直接写本线程键盘状态表，实测 `GetKeyState(VK_CONTROL)=0xFF80 高位=True` 并让 `N` 变为 `eaten=False`。这样做**语义上等价**，理由是 `TextService.HasControlOrAlt()` 唯一的实现就是 `(GetKeyState(vk) & 0x8000) != 0`（同一线程局部状态），所以两者对它不可区分；但严格说「真的用手指/真实输入注入按住 Ctrl」这条路径没有跑通，本条以「未验证真实注入、已验证它读取的那份状态」的形式记账。
4. **`panel.png` 的视觉内容未经确认。** 本模型不支持图像输入（`read_image` 返回 "does not declare image input"）。只做了像素统计（Pillow 12.3.0）：163x98、100 种颜色、主色白 `(255,255,255)` 8860 px、选中底色 `(0,120,215)` 4931 px、非背景像素 7114、包围盒覆盖整个窗口。这**能证明窗口被真实绘制出来了**（不是全黑/空白帧），**不能证明文字渲染正确**。另外 `PrintWindow` 与屏幕 `BitBlt` 两条路径给出的非黑像素数完全相同（2662），在有 `WM_PRINTCLIENT` 处理的情况下这是合理的，但仍属「未独立交叉确认」。
5. **`TrySetTextAtSelection` 回退路径未被覆盖。** 因为主路径 `TryInsertAtSelection` 一次就成功，`TextService.cs:545-577` 的退路在本次运行中从未执行（可查 `SetText[DOC-WRITE]` 事件从未出现）。
6. **被测源码在我写 harness 期间被并发修改。** `src\Xingbian.Tsf\TextService.cs` 从最初读取的 937 行变成 1013 行、再变成 1020 行（`_panelClickHooked`/`_uiThreadId`/`OnPanelItemClicked`/`GetCurrentContext` 等是期间被加进来的）。本报告引用的行号对应**1020 行版本**。harness 只依赖稳定的公开接口面，最终构建编译的是磁盘上的当时版本。
7. **没有做压力/长时运行/多文档多线程测试**；也没有验证 `Deactivate()` 之后 TSF 侧的状态（仅确认 18 个实例的 `Deactivate()` 均正常返回）。
8. **没有验证 `ITfThreadMgrEventSink` 的 `OnSetFocus`/`OnPushContext` 在真实焦点切换下的行为**（仅在注册时被 TSF 调用过）。

## ⑥ 发现的 `TextService.cs` 真实缺陷

以下两条都是**实测复现**出来的，不是读代码猜的。按要求**没有修改 `src\` 下任何文件**。

### 缺陷 1：`pic == null` 时仍然吞键，输入被静默丢弃

* **位置**：`src\Xingbian.Tsf\TextService.cs:307-325`（`OnKeyDown` 在 `ShouldEat` 通过后立即 `pfEaten = true;`，完全没有检查 `pic` 是否为 `null`）与 `src\Xingbian.Tsf\TextService.cs:736-738`（`InsertIntoDocument` 开头 `if (pic == null) { return false; }`）。
* **现象**：`pic` 为 `null` 时字母键与功能键都照吞，但文本永远不会进文档 —— 按键被吃掉了、宿主收不到、IME 也没写出任何东西，用户的输入**凭空消失**。
* **复现**（用例 10 的实际输出，`RESULTS.md` 内可查）：
  ```
  OnTestKeyDown(null, VK_N)   -> hr=0x00000000 eaten=True
  OnKeyDown(null, VK_N)       -> hr=0x00000000 eaten=True     // buffer 变成 "n"，但无处可写
  OnKeyDown(null, VK_SPACE)   -> hr=0x00000000 eaten=True     // 试图提交，InsertIntoDocument 因 pic==null 返回 false
  ```
  末行的 `eaten=True` 就是缺陷：这条空格既没有被上屏、也没有被宿主收到。
* **可达性（推断，未验证）**：`ITfKeyEventSink` 回调的 `pic` 允许为 `NULL`（无焦点文档 / 焦点文档没有 text store 时），所以这条路径很可能在真实环境可达。harness 无法验证真实环境是否真的会送 `NULL` 进来（TIP 未注册），因此只把「代码没有防御」这一点记为已证事实，「线上是否触发」记为未验证。
* **影响面**：与「异常安全」用例其实是一体两面 —— 返回 `S_OK`、不抛异常这条承诺是守住了（用例 10 因此 PASS），但代价是把失败藏进了「吞键」里。**建议（不代改）**：`OnKeyDown` 里在 `ShouldEat` 之后、置 `pfEaten = true` 之前加一条 `if (pic == null) { return HResult.S_OK; }`，或者让 `ShouldEat` 在 `pic == null` 时返回 `false`。

### 缺陷 2：`_shiftChord` 闩锁 —— 退格/功能键之后紧接着的第一次 Shift 单击无效

* **位置**：`src\Xingbian.Tsf\TextService.cs:325`（`OnKeyDown` 对任何「非字母但被吞」的键都置 `_shiftChord = true;`，且这是它唯一被置位、却只有 Shift 的 `OnKeyUp`（第 410-411 行）和 `ResetInput()`（第 723-724 行）会清它的地方）、`src\Xingbian.Tsf\TextService.cs:428`（`OnTestKeyUp`：`if (!_shiftChord) { _shiftPending = true; }`）、`src\Xingbian.Tsf\TextService.cs:404`（`OnKeyUp`：`if (_shiftPending && !_shiftChord) { 切换 }`）。
* **现象**：按完退格（缓冲区已清空）之后，单击一次 Shift 完全不生效，必须点第二次才切换中/英。原因是退格路径 `Backspace`（第 626-642 行）在 `_buffer.Length == 0` 时直接 `HidePanel(); return;`，**不会**调用 `ResetInput()`，于是第 325 行留下的 `_shiftChord = true` 一直在那儿；下一次 Shift：`OnTestKeyUp` 看到 `_shiftChord == true` 就不置 `_shiftPending`，`OnKeyUp` 于是只清标志、不切换。
* **复现**（用例 8b 的实际输出）：
  ```
  8b 起点：中文模式 = True
  8b 敲 n（eaten=True）再退格（eaten=True）：缓冲区已被退格清空，但 OnKeyDown 里 _shiftChord 已被置 true 且无人清除
  8b 第 1 对 Shift 后：敲 N 被吞 = True（这一次单击被闩锁吞掉了）
  8b 第 2 对 Shift 后：敲 N 被吞 = False（第 2 次才切换）
  ```
* **注意与 8a 的对照**：全新实例（`_shiftChord` 初值 `false`）上，第 1 对 Shift 就正常切换（8a 实测 `True → False → True`）。差别只在「之前是否按过被吞的功能键」。成功上屏的路径不受影响，因为 `CommitSelected` 成功后会 `ResetInput()` 清掉闩锁。
* **影响面**：真实可感的输入法体验缺陷（「退格删空后想切英文，第一下没反应」）。**建议（不代改）**：在 `Backspace` 把 buffer 清空的那条分支里补一次 `_shiftChord = false;`（或直接调 `ResetInput()`），或者把 `_shiftChord` 的语义收窄到「本次按键序列里出现过非 Shift 的可见按键」。

### 另外两条值得记录但未升级为缺陷的观察

* `ShouldEat`（`TextService.cs:586-613`）在 `_chineseMode == true` 时无条件吞 a-z，**不检查词典是否加载成功**；而 `AppendLetter`（第 615-624 行）开头就是 `if (EnsureEngine() == null) { return; }`。两者叠加的后果是：**词典加载失败时，字母键全部被吞且什么也不发生**，用户表现为「键盘打不出英文」，且 `RefreshCandidates` 的 `catch` 会把 `_candidates` 静默置 `null`（第 707-712 行）。本次运行词典正常（`Warmup -> True`、`DictPath` 正确、4376418 字节），所以**未实测复现**，仅从代码路径推出，如实标注为「未验证的推断」。
* `DoEditSession`（第 478-493 行）**永远返回 `S_OK`**、把真实结果藏在 `_pendingInsertHr` 里 —— 这是有意的设计（注释说明了理由：返回失败会让 TSF 认为编辑会话本身出错并可能反复重试），本次运行也证实了这条约定工作正常；但它与缺陷 1 结合时会让失败更难被上层察觉。

## 附：假文本存储的自我一致性

假存储 `FakeTextStore` 的关键实现选择（都写进了 `scratch\e2e\FakeTextStore.cs`，便于复核）：

* `GetStatus`（`FakeTextStore.cs:209`）返回 `dwDynamicFlags = 0`，**故意不上报 `TS_SD_READONLY`** —— 否则 `Context_RequestEditSession` 会在第 239-243 行直接短路成 `TS_E_READONLY`，链路根本走不到插入。
* `RequestLock`（`FakeTextStore.cs:137`）模型 A 的正文：置 `*phrSession = S_OK` → 递增 `OnLockGrantedCalls` → **同步**调用 `_sink.OnLockGranted(dwLockFlags)` → 若 `TS_LF_SYNC` 被置位则递减 `_lockDepth` → 返回 `S_OK`。
* `AdviseSink`（`FakeTextStore.cs:109`）用 `Marshal.GetObjectForIUnknown(punk)` 再 `as ITextStoreACPSink` 拿 sink，拿不到就返回 `E_NOINTERFACE`。
* `GetWnd`（`FakeTextStore.cs:488`）返回 harness 用 `RegisterClassExW`+`CreateWindowExW` 真实创建的隐藏 `WS_POPUP` 宿主窗口句柄（本次运行 `hwnd=0xD90974`），而不是 `NULL`。
* 观测口径：`Text`（存储当前文本）、`TextLength`、`RequestLockCalls`、`OnLockGrantedCalls`、`InsertTextAtSelectionCalls`、`LastLockFlags`、`LockDepth`、`Events`。断言只读这些，不读被测对象的任何私有字段。

