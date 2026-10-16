# 形变输入法 (xingbian-ime)

Windows x64 上的中文输入法：把词语按**相近形体的字**替换显示 —— 输入的每个字被拆成它的
IDS（Ideographic Description Sequence）一级部件。

```
你好   →  亻尔 女子
```

`你 = ⿰亻尔`、`好 = ⿰女子`，所以「你妈」形变成「亻尔 女子」。这是**真 TSF 文本服务**
（出现在 `Win + 空格` 输入法列表里），不是热键外挂。

---

## 目录结构

```
xingbian-ime\
├── Directory.Build.props      net48 / x64 / LangVersion 7.3 的统一构建约定
├── nuget.config               只用仓库内本地离线源 .nuget-local
├── docs\FORMAT.md             词典 xingbian.bin 的二进制格式契约（v1，冻结）
├── assets\
│   ├── raw\                   19 个上游数据源（CHISE IDS / Unihan / Rime / jieba / OpenCC）
│   └── build\xingbian.bin     数据管线产物（约 4.17 MB）
├── tools\
│   ├── fetch_data.py          拉取并校验上游数据源
│   ├── build_dict.py          数据管线，--selftest 有 16 项自检
│   └── data_report.md         数据侧勘察报告（9 章，含第三方复核）
├── src\
│   ├── Xingbian.Core\         词典读写 + 拼音切分 + 形变格式化（纯逻辑，无 Win32 依赖）
│   ├── Xingbian.Cli\          命令行工具，可脱离输入法直接把玩词典
│   └── Xingbian.Tsf\          TSF 文本服务本体（in-proc 托管 COM 服务器）
├── install\                   注册 / 卸载脚本 + 安装说明
└── scratch\                   开发期的验证工程与取证脚本（非交付物）
```

---

## 快速开始

### 1. 编译

```powershell
dotnet build src\Xingbian.Tsf\Xingbian.Tsf.csproj -c Release
dotnet build src\Xingbian.Cli\Xingbian.Cli.csproj -c Release
```

前提：.NET SDK 8（用来编译 net48 目标）+ 仓库内 `.nuget-local` 里的
`Microsoft.NETFramework.ReferenceAssemblies`。不需要 Visual Studio，不需要 Windows SDK。

### 2. 先不装输入法，直接试词典

```powershell
# 形变本身
Xingbian.Cli.exe --transform 你妈
#   词典: ...\assets\build\xingbian.bin
#   输入: 你妈
#   形变: 亻尔 女马

# 拼音查候选（最后一列是形变结果）
Xingbian.Cli.exe --input nima
#   输入 "nima" 的候选（2 条）:
#   序号  词频      可拆  原词            形变
#      1    960000  是     你妈              亻尔 女马
#      2    567730  是     尼玛              尸匕 王马

# 全量自检（14 项）
Xingbian.Cli.exe --selftest

# 加载耗时 / 内存 / 查询吞吐
Xingbian.Cli.exe --bench
```

### 3. 安装成真正的输入法

**需要管理员权限**（要写 `HKLM`）。普通用户运行会以退出码 `2` 明确报错，不会静默失败。

```powershell
# 右键「以管理员身份运行」，或直接双击（.cmd 会自提升）
install\install.cmd
```

装完 **重启 `ctfmon`**（或注销重登），然后 `Win + 空格` 切到「形变输入法」。

详细步骤、排障、手工兜底见 [`install\README.md`](install/README.md)。

---

## 怎么打字

| 按键 | 行为 |
|---|---|
| `a`–`z` | 累积拼音到**候选浮窗**（不写进文档） |
| `1`–`9` | 直接选第 N 个候选并上屏 |
| `空格` / `回车` | 上屏当前选中的候选 |
| `↑` `↓` | 上下移动选择 |
| `PgUp` `PgDn` | 翻页（步长 5） |
| `Home` `End` | 跳到首 / 末候选 |
| `退格` | 删掉一个拼音字母 |
| `Esc` | 取消本次输入 |
| `Shift`（单击） | 中 / 英切换 |

鼠标点候选浮窗上的任意一行也能上屏。

**v1 不做 TSF composition**：拼音串显示在浮窗里，**不会**出现在文档中，所以打字过程中
文档里没有下划线预编辑串，光标也不动。选定后一次性把结果文本插入文档。这样做的原因是
composition 链路（`RequestEditSession` → `StartComposition` → 每键 `ShiftStart`/`ShiftEnd`
→ display attribute）在真实宿主里失败模式多，且一旦 `EndComposition` 没走到就会锁死文档。

---

## 设计要点

### 词典是「音节 → 词 → 形变」的三段式

`xingbian.bin` 三个数据块（格式契约见 `docs\FORMAT.md`）：

| 块 | 内容 | 规模 |
|---|---|---|
| 1 | 无调拼音音节表（行号即音节 ID） | 429 条 |
| 2 | 词表（音节 ID 序列 → 词 + 频次） | 200 000 条 |
| 3 | IDS 拆解表（码点 → 一级部件） | 88 480 条 |
| 4 | 元信息（JSON，读取端可忽略） | — |

索引在加载时按「音节 ID 序列」建 key，不存音节文本；同一词只留最高频次。
实测：加载约 237 ms，常驻约 52 MB，单次查询约 6 μs。

### 拼音切分必须两趟

第一趟只查**精确**切分（`ni` → 你 / 尼 / 泥）；第一趟为空才把**末音节展开成前缀**
（`nim` → `ni` + `m` → ma / mai / man / …）。原因是真实音节表里有单字母音节
（`a` `e` `m` `n` `o`，嗯 = `n`、呣 = `m`），若无条件展开会污染 `ni` 的正常结果。

### 数据侧做过繁→简归一化

上游 `essay.txt` 是**繁体**语料，频次量级高达 50–85 万，不归一化会压过 jieba 的简体条目
—— 实测过 `nima` 只能查到「你媽」，简体「你妈」根本不在词表里。现在用 OpenCC
`TSCharacters` 逐字归一化后再按 (音节序列, 简体词) 合并取最大频次。

同时用 OpenCC **TSPhrases 词组表**挡住「过度归一化」：`乾` 的首选简体是 `干`，
逐字硬套会得到 `乾隆 → 干隆`、`乾坤 → 干坤`。词表自检第 16 项专门盯这个。

### 候选浮窗是纯 Win32

`CandidatePanel` 只用 `user32.dll` / `gdi32.dll`，**不依赖 WinForms / System.Drawing / WPF**。
理由：TSF 文本服务被注入到大量宿主进程里，拖进 WinForms 或 GDI+ 会污染每个宿主，
在低完整性 / 沙箱化宿主里还可能加载失败。这条约束由 `scratch\verify-com\` 的
反射校验器守着（30 项）。

浮窗是 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`，`WM_MOUSEACTIVATE`
返回 `MA_NOACTIVATE`（**不是** `MA_NOACTIVATEANDEAT` —— 后者会吃掉整次点击导致点选失效），
显示用 `SW_SHOWNOACTIVATE`，**绝不** `SetForegroundWindow`。

---

## 验证到了什么程度

诚实清单 —— 分「已实测」和「未实测」两栏。

### 已实测

- **数据管线**：`tools\build_dict.py --selftest` 16 项全过；连跑两次产物逐字节相同（幂等）；
  独立校验器（不复用管线读端）0 失败。
- **C# 读取端**：`Xingbian.Cli --selftest` 14 项全过，含多线程并发一致性。
  `--transform 你妈` 精确等于 `亻尔 女马`。
- **COM 契约**：`scratch\verify-com\` 反射校验器 30 项全过 —— 程序集标识、`TextService` 的
  `[ComVisible]` / `[Guid]` / `[ClassInterface(None)]`、5 个 TSF 接口、
  以及**逐个 IID 的真实 `Marshal.QueryInterface`**。
- **托管类能否当 TSF 回调**：`scratch\ccwtest\` 证明托管类实现 `[ComImport]` 接口后
  能在 CCW 上被原生侧 QI 到，IID 恰为 `ITfKeyEventSink` 的 `aa80e7f5-…`。
  这是整个架构的地基（否则 TSF 回调根本走不通）。
- **候选浮窗**：24 项 PASS —— 不抢焦点（`Show` 前后前台窗口句柄不变）、GDI/USER 句柄零泄漏、
  多显示器工作区钳制、脏输入不抛、中文渲染经 `GetGlyphIndicesW` 覆盖查询确认不是豆腐块
  （13 个探针码位缺失 0/13）。
- **接口 IID 与方法序**：逐条核对 Windows SDK 10.0.16299.0 原版头文件
  （解析 `struct IXXXVtbl` 成员顺序，扣除 `IUnknown` 三槽）。
- **CCW vtable 槽位（曾为致命缺陷，已修复并实证）**：`ITfTextInputProcessorEx` 若按 C#
  接口继承形状声明，CLR 会把 `ActivateEx` 绑到槽 3、槽 4/5 留成 pre-code 垃圾，
  TSF 调槽 5 即宿主崩溃。改为**扁平声明 + 实现类同时列出两个接口**后，
  `scratch\vtable-real\` 用哨兵指纹逐槽真调实测：槽 3/4/5 分别落到
  `Activate`（E_INVALIDARG）/`Deactivate`（S_OK）/`ActivateEx`（E_INVALIDARG）。
- **注册表路径的 COM 激活**：`scratch\cocreate\` 原生级探针 9 项全过 ——
  `CoGetClassObject`/`CoCreateInstance` 经真实 HKLM `InprocServer32 → mscoree.dll`
  建出托管类，QI 四个接口全 S_OK，直读 vtable 调槽 3/4/5 行为全部正确。
- **真实系统安装**：`install\install.cmd` 提权真跑，5 步全部 `[OK]` 且逐项回读校验通过，
  `InstallLayoutOrTip` 成功，输入法出现在 Win+空格 列表
  （`0804:{7C4E1A9B-…}{B3F2C8D1-…}`）。
- **💥 真实宿主端到端（决定性）**：`scratch\realhost\` —— 真记事本 + 真实 SendInput
  按键 + `ITfInputProcessorProfileMgr::ActivateProfile` 切到本输入法 + 跨进程读回。
  实测：敲 `nima` 时候选浮窗出现在**记事本进程内**（pid 与记事本相同，
  即托管 TextService 真的被宿主加载并运行了），按空格后记事本内容精确为
  **`亻尔 女马`**（U+4EBB U+5C14 U+0020 U+5973 U+9A6C）。5 项全过。
- **状态机回归（29 项）**：`scratch\lead-e2e\` 用假 TSF 上下文驱动**真实交付物 DLL**，
  含主链路、数字选候选、Esc、退格、Shift 切中英、SetText 后备路径、Deactivate 注销，
  以及两个真实缺陷的回归用例（pic=null 不吞键、退格后 Shift 不闩锁）。
  另实测首键词典加载约 207ms → 已改为 Activate 时后台预热。

### 未实测（环境限制）

- **`-PerUser`（HKCU 免管理员）路线。** HKCU 下的 `CTF\TIP` 结构本机确实存在
  （微软拼音就有），但那是系统自己写的，不代表第三方 TIP 走 HKCU 能被采纳。
- **`InstallLayoutOrTip` 的 `ILOT_UNINSTALL` 常量值**取自多处公开 SDK 转写
  （corvusskk、Vanara、rime/weasel、VietType 四处一致为 `0x00000001`），
  **不是**从本机 Windows SDK 头文件读到的 —— 本机没装 Windows SDK，
  且 `input.h` 在所有可达的公开镜像里都取不到（该 API 无官方文档页）。
- **卸载→重装循环的真机验证**：注册脚本在内存假注册表 harness 上验证过
  30/30（注册）+17/17（卸载）+ 卸载后重装仍 30/30，但真机卸载链路未跑过。
- **Windows 11**。用的是同一套 TSF，理论上兼容，没实测。

---

## 环境要求

| 项 | 要求 |
|---|---|
| OS | Windows 10 x64（22H2 上开发）/ Windows 11 理论兼容 |
| 运行时 | .NET Framework 4.8（系统自带） |
| 编译 | .NET SDK 8（仅用于编译 net48 目标）+ 仓库内 `.nuget-local` |
| 输入法框架 | TSF（`msctf.dll`，系统自带） |
| 安装 | **管理员权限**（写 HKLM） |

---

## 相关文档

- [`docs\FORMAT.md`](docs/FORMAT.md) —— `xingbian.bin` 二进制格式契约（v1）
- [`install\README.md`](install/README.md) —— 安装 / 卸载 / 排障 / 手工兜底
- [`tools\data_report.md`](tools/data_report.md) —— 数据源勘察与取舍报告
