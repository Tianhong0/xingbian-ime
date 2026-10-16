# 形变输入法 (xingbian-ime) —— 安装与卸载

一个 Windows x64 的 TSF 文本服务（TIP）中文输入法。把词语按字形拆成部件再上屏：

```
nima  →  空格  →  亻尔 女马
```

底层是 IDS（Ideographic Description Sequence）表意文字描述序列，只拆一层：
`你 = ⿰亻尔` → `亻尔`，`妈 = ⿰女马` → `女马`。不做递归拆解。

---

## 0. 系统要求

| 项目 | 要求 |
|---|---|
| 操作系统 | **Windows 10 x64**（TSF 需要系统自带的 `input.dll` 与 CTF 运行时；Windows 11 同一套 TSF，理论兼容但未实测） |
| 运行时 | **.NET Framework 4.8**（Windows 10 1809 及以后自带，无需单独安装） |
| 架构 | **x64**。本输入法是 x64 的托管 in-proc COM 服务器，32 位宿主不会加载它 |
| 权限 | **管理员**。默认 HKLM 路线必须提权，否则以退出码 2 结束 |
| 其他 | 安装脚本只用系统自带的 **Windows PowerShell 5.1**。不需要 Windows SDK、Visual Studio 或 .NET SDK —— 这些只在你自己重新编译时才需要 |

> 检查 .NET Framework 版本：
> ```powershell
> (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release
> ```
> 返回值 ≥ `528040` 即为 4.8 或更高。

---

## 1. 准备工作：先编译

安装脚本**不编译**，它只搬运已经编译好的产物。所以先出 Release：

```powershell
cd <仓库根目录>
& "C:\Program Files\dotnet\dotnet.exe" build src\Xingbian.Tsf\Xingbian.Tsf.csproj -c Release
& "C:\Program Files\dotnet\dotnet.exe" build src\Xingbian.Cli\Xingbian.Cli.csproj -c Release
```

需要三个东西：

| 文件 | 默认位置（相对仓库根） |
|---|---|
| `Xingbian.Tsf.dll` | `src\Xingbian.Tsf\bin\Release\Xingbian.Tsf.dll` |
| `Xingbian.Core.dll` | `src\Xingbian.Core\bin\Release\Xingbian.Core.dll` |
| 词典 `xingbian.bin` | `assets\build\xingbian.bin` |

词典如果还没有，用数据管线从原始语料重建（约 20 秒）：

```powershell
python tools\build_dict.py --selftest
```

> **`Xingbian.Core.dll` 必须和 `Xingbian.Tsf.dll` 放在同一个目录**，否则 CLR 加载 `TextService` 会失败。安装脚本会自动一起复制。

---

## 2. 安装

**双击 `install.cmd` 即可**（会弹 UAC，点「是」）。它做的事：

1. 用 `Start-Process -Verb RunAs` 自提权，再调用 `register.ps1`
2. 复制两个 dll 到安装目录根、词典到 `<安装目录>\dict\xingbian.bin`
3. 写 `HKLM\SOFTWARE\Classes\CLSID\{CLSID}\InprocServer32`（托管 in-proc COM 服务器）
4. 写 `HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\LanguageProfile\0x00000804\{ProfileGUID}`
5. 写 4 个 TSF Category
6. 写 HKCU 两项：用户级启用位 `Enable=1`，以及词典路径 `DictPath`（见 §5.5）
7. 调 `input.dll!InstallLayoutOrTip` 把输入法挂进语言列表（**失败只警告**，注册表已写好，可手工添加）

**每一处注册表写入都会写完立刻回读校验**，任何一项对不上就以退出码 4 中止并列出是哪一项。

### 命令行用法

```powershell
# 默认路线（HKLM，需要管理员）
.\register.ps1

# 只写注册表，不复制文件
.\register.ps1 -SkipCopy

# 换个安装目录
.\register.ps1 -InstallDir 'D:\XingbianIME'

# 从别处取构建产物
.\register.ps1 -Source 'D:\build' -Dict 'D:\build\xingbian.bin'

# 安静模式：只打印警告和错误
.\register.ps1 -Quiet
```

| 参数 | 默认值 | 说明 |
|---|---|---|
| `-PerUser` | 关 | **实验路线，本机未验证**，见 §5 |
| `-InstallDir` | `C:\Program Files\XingbianIME` | 安装目录 |
| `-Source` | `<仓库>\src\Xingbian.Tsf\bin\Release` | 构建产物目录 |
| `-Dict` | `<仓库>\assets\build\xingbian.bin` | 词典文件 |
| `-SkipCopy` | 关 | 只写注册表，不复制任何文件 |
| `-Quiet` | 关 | 只输出警告和错误 |

### 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 2 | 权限不足（HKLM 路线需要管理员） |
| 3 | 源文件缺失（没编译，或路径不对） |
| 4 | 注册表写入或回读校验失败 |

---

## 3. 装完之后必须做的一步：让 TSF 重新加载

`ctfmon.exe` 在启动时读 TSF 注册表，之后就一直用内存里的那份。所以**必须**让它重读一次，二选一：

```powershell
# 快一点：重启 ctfmon（系统会自动把它拉起来）
Stop-Process -Name ctfmon -Force -ErrorAction SilentlyContinue
```

**最稳妥**：注销并重新登录，或重启电脑。

### 然后验证

1. 按 `Win + 空格` 切换输入法，列表里应该能看到「**形变输入法**」
   （在中文(简体) 那一组下面）
2. 打开记事本，敲 `nima`，应弹出候选浮窗
3. 按 **空格** 上屏 → 得到 `亻尔 女马`

### 操作说明

| 按键 | 作用 |
|---|---|
| `a`–`z` | 输入拼音，实时出候选 |
| `1`–`9` | 直接选第 n 个候选上屏 |
| `空格` / `回车` | 上屏当前选中的候选 |
| `↑` `↓` | 上下移动选择 |
| `PgUp` / `PgDn` | 翻页（每页 5 个） |
| `Home` / `End` | 跳到第一个 / 最后一个候选 |
| `退格` | 删一个拼音字母（不是删文档） |
| `Esc` | 取消本次输入 |
| `Shift`（单击） | 切换中/英文 |

> 缓冲区为空时，空格 / 回车 / 退格**原样交给宿主**，不会被打字法吃掉 —— 所以在记事本里正常换行、删字都不受影响。
> 按住 `Ctrl` 或 `Alt` 时也不接管按键，`Ctrl+C` / `Ctrl+V` 等照常工作。

---

## 4. 卸载

**双击 `uninstall.cmd`**（同样会弹 UAC），或：

```powershell
# 删注册，保留安装目录（默认，方便排障）
.\unregister.ps1

# 连安装目录一起删
.\unregister.ps1 -RemoveFiles
```

卸载顺序是**先**调 `InstallLayoutOrTip(psz, ILOT_UNINSTALL = 0x00000001)` 把它从语言列表摘掉，**再**删注册表 —— 反过来的话 `psz` 就解析不到了。

HKCU 和 HKLM 两处都会清理，所以能同时干掉以前 `-PerUser` 装出来的残留。项不存在不算错误（幂等）。

| 参数 | 默认值 | 说明 |
|---|---|---|
| `-PerUser` | 关 | 只影响提示措辞；实际清理时两处都会试 |
| `-InstallDir` | `C:\Program Files\XingbianIME` | 安装目录 |
| `-RemoveFiles` | 关 | 同时删除安装目录 |
| `-Quiet` | 关 | 只输出警告和错误 |

---

## 5. 手工安装（脚本失败时的兜底）

如果脚本因为权限或回读校验失败，可以照着下面手工建。把 `{CLSID}` 换成
`{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}`，`{ProfileGUID}` 换成
`{B3F2C8D1-6A47-4C09-8E52-1D7A9F0B4C65}`。

> 这两个 GUID 的权威来源是 `src\Xingbian.Tsf\XingbianTsfIds.cs`，以及编译产物
> `Xingbian.Tsf.dll` 上 `[Guid]` 特性的实际值 —— **二者必须一致**。注册脚本会在写
> 任何注册表项之前核对它们，不一致直接中止。

### 5.1 COM 注册

```
HKLM\SOFTWARE\Classes\CLSID\{CLSID}
    (默认)              = 形变输入法
  \InprocServer32
    (默认)              = C:\Windows\System32\mscoree.dll
    ThreadingModel      = Both
    Assembly            = Xingbian.Tsf, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null
    Class               = Xingbian.Tsf.TextService
    RuntimeVersion      = v4.0.30319
    CodeBase            = file:///C:/Program Files/XingbianIME/Xingbian.Tsf.dll
```

- `InprocServer32` 指向 **`mscoree.dll`**（CLR 的 COM 垫片），不是我们自己的 dll
- `Assembly` 必须**用真实编译产物的程序集标识**（脚本会从 dll 读，读不到才退回上面的常量）
- `CodeBase` 是 `file:///` + 安装目录（反斜杠换成正斜杠）+ dll 名

### 5.2 TSF 语言配置

```
HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\LanguageProfile\0x00000804\{ProfileGUID}
    (默认)              = 形变输入法
    Description         = 形变输入法
    Display Description = 形变输入法
    IconFile            = C:\Program Files\XingbianIME\Xingbian.Tsf.dll
    IconIndex           = 0        (DWORD)
    Enable              = 1        (DWORD)
```

> `0x00000804` 是 LANGID zh-CN 的键名形式（`0x` + 8 位十六进制）。

### 5.3 四个 TSF Category

每个 Category 要建**两处**空键：

```
HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\Category\Category\{CATGUID}\{CLSID}
HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\Category\Item\{CLSID}\{CATGUID}
```

`{CATGUID}` 取这四个：

| GUID | 含义 |
|---|---|
| `{34745C63-B2F0-4784-8B67-5E12C8701A31}` | `GUID_TFCAT_TIP_KEYBOARD`（键盘输入处理器，必需） |
| `{49D2F9CE-1F5E-11D7-A6D3-00065B84435C}` | `GUID_TFCAT_TIPCAP_UIELEMENTENABLED` |
| `{13A016DF-560B-46CD-947A-4C3AF1E0E35D}` | `GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT` |
| `{25504FB4-7BAB-4BC1-9C69-CF81890F0EF5}` | `GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT` |

> **刻意不注册** `GUID_TFCAT_TIPCAP_COMLESS` `{364215D9-75BC-11D7-A6EF-00065B84435C}`
> 与 `GUID_TFCAT_TIPCAP_SECUREMODE` `{49D2F9CF-1F5E-11D7-A6D3-00065B84435C}`：
> 它们是给免 COM 和安全桌面（登录界面）用的，声明了却不具备相应能力会导致
> 安全桌面/登录界面加载失败。

### 5.4 HKCU 用户级启用位

```
HKCU\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\LanguageProfile\0x00000804\{ProfileGUID}
    Enable = 1     (DWORD)
```

### 5.5 词典路径（双保险）

```
HKCU\Software\XingbianIME
    DictPath = <安装目录>\dict\xingbian.bin     (REG_SZ)
```

词典本来靠「程序集所在目录\dict\xingbian.bin」就能找到，这一项是**双保险**：
以后哪怕 dll 被挪到别处，只要词典还在这台机器上就仍能找到。

> ⚠️ **已知现象**：真实提权安装后，§5.4 的 `Enable` 位**不一定能写进去**。
> 同一次运行里，上面这个 `DictPath` 写成功了，而 §5.4 的那一项没有 ——
> 两者用的是同一段 .NET 代码，所以差异出在 `...\CTF\TIP` 这棵子树上。
> **已排除**「权限不足」这一解释：实测该键给当前用户的 ACE 是
> `KEY_ALL_ACCESS`（`ContainerInherit, ObjectInherit`），权限是够的。
> **确切原因尚未查明**，所以这里只记录现象，不编机制（详见 §8 第 8 条）。
> 缺 §5.4 不影响主注册，输入法通常照样能用；但脚本会把它当成回读校验失败并以
> **退出码 4** 结束 —— 这是故意的。

### 5.6 文件

```
C:\Program Files\XingbianIME\
    Xingbian.Tsf.dll
    Xingbian.Core.dll
    dict\xingbian.bin
```

词典路径的查找顺序（`XingbianEngine.CreateDefault()`）：

1. 环境变量 `XINGBIAN_DICT`
2. 注册表 `HKCU\Software\XingbianIME` 的 `DictPath`（**安装脚本会写这一项**，见 §5.5；
   也可以随时手改它来指向你自己的词典）
3. `<Xingbian.Tsf.dll 所在目录>\dict\xingbian.bin` ← 正常安装走这条
4. `<Xingbian.Tsf.dll 所在目录>\xingbian.bin`

---

## 6. `-PerUser` 实验路线（本机未验证）

```powershell
powershell -ExecutionPolicy Bypass -File .\register.ps1 -PerUser
```

它把本该写进 HKLM 的两棵子树（`Software\Classes\CLSID\{CLSID}` 和
`SOFTWARE\Microsoft\CTF\TIP\{CLSID}`）原样写进 HKCU，因而不需要管理员权限。

**⚠️ 这条路线是否会被 TSF 采纳，我们没有验证过。** 已知 HKCU 也有 TSF 注册结构
（本机微软拼音就在 HKCU 下有一条），但那是系统自己写的，不代表第三方 TIP 走
HKCU 能被加载。如果装完 `Win + 空格` 里看不到「形变输入法」，请改用默认 HKLM 路线。

---

## 7. 排障

| 现象 | 原因 / 处理 |
|---|---|
| `Win + 空格` 里没有「形变输入法」 | 没重启 `ctfmon`（见 §3）；或 `InstallLayoutOrTip` 失败且语言列表没挂上，手工添加：设置 → 时间和语言 → 语言 → 中文(简体) → 选项 → 添加键盘 |
| 列表里**有**但一选就崩 / 切不过去 | CLSID 与 dll 上 `[Guid]` 不一致（注册到了没人实现的 CLSID），重新跑 `register.ps1`；或 `Xingbian.Core.dll` 不在同目录 |
| 敲字母完全没反应 | 当前不在「形变输入法」下，按 `Win + 空格` 切过去；或单击 `Shift` 切回中文 |
| 候选窗不显示但能上屏 | 面板窗口创建失败（`LastError`）；不影响上屏 |
| 退出码 4 | 看管理员窗口里列出的具体是哪一项回读不符 |
| 装完想彻底重来 | 先 `unregister.ps1 -RemoveFiles`，再重新 `install.cmd` |

---

## 8. 已知限制

1. **必须用管理员权限安装。** 默认路线要写 HKLM，普通用户跑会以退出码 `2` 结束并给出提示，
   不会静默失败。

2. **`-PerUser`（HKCU 免管理员）路线在本机未验证。**
   它把本该写进 HKLM 的两棵子树原样写进 HKCU，
   注册表**结构**与系统自己写的 `HKCU\SOFTWARE\Microsoft\CTF\TIP` 是一致的，
   但 **TSF 是否会采纳 HKCU 下的 `CLSID` 子树，本机没有验证过**。
   这条路线属于实验性质：如果 Win + 空格 里看不到「形变输入法」，请改用默认的管理员路线。

3. **这是 v1，不做 TSF composition。**
   拼音串显示在**候选浮窗**里，**不会**显示在文档内 —— 也就是说没有下划线预编辑串
   （composition string），文档里的光标位置在打字过程中不会跟着变。
   上屏时直接把结果文本插入文档。相应地，`Esc` / `退格` 的行为只在浮窗里生效，
   不会反映到文档中。

4. **不支持安全桌面和登录界面。** 没有注册
   `GUID_TFCAT_TIPCAP_COMLESS` 与 `GUID_TFCAT_TIPCAP_SECUREMODE`，这是有意为之：
   声明了却没有相应能力会导致安全桌面 / 登录界面加载失败。

5. **只在 Windows 10 x64 + .NET Framework 4.8 上验证过。**
   Windows 11 用的是同一套 TSF，理论上兼容，但没有实测。

6. **`IconIndex` 固定为 0，`IconFile` 指向 DLL 自身。**
   v1 的 DLL 里没有图标资源，输入法列表里可能显示成默认图标。

7. **卸载默认不删文件。** 要清理安装目录必须显式加 `-RemoveFiles`，
   这是为了脚本失败时还能直接看 DLL 和词典来排障。

8. **HKCU 步骤失败时会按退出码 `4` 报失败，而不是假装成功。**
   **实测现象**：`install.cmd` 那次真实提权安装结束时，HKLM 下的注册项全部写成功，
   但 `HKCU\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\...\Enable` 并不存在。
   **已排除**「权限不足」：实测该键给当前用户的 ACE 是 `KEY_ALL_ACCESS`
   （`ContainerInherit, ObjectInherit`，propagation = `None`），权限是够的；
   同一次运行里 `HKCU\Software\XingbianIME\DictPath` 也确实写成功了。
   **确切原因尚未查明**，所以这里只陈述现象、不编机制。
   这一步只是「用户级启用位」，缺了它**不影响注册表主注册**，输入法通常照样能用；
   但脚本会把它当成回读校验失败并以退出码 `4` 结束 —— 这是**故意**的，
   宁可报错也不要出现「说装好了其实没装全」的静默失败。
   遇到这种情况可以不管，或手工补上该项（见 §5.4）。

---

## 9. 这个安装包**验证到什么程度**

诚实说明，避免误判：

**已实测**
- `register.ps1` / `unregister.ps1` 在 Windows PowerShell 5.1 下语法解析 0 错误
- 两个脚本均为 **UTF-8 with BOM**（PS 5.1 无 BOM 会把中文读成乱码）
- 脚本里的 CLSID / ProfileGUID / 类全名 / 描述与 `XingbianTsfIds.cs`、以及
  `Xingbian.Tsf.dll` 上 `[Guid]` 的实际值逐字符一致
- 注册表结构逐项对照本机微软拼音 TIP 的真实结构
- **`install.cmd` → 提权 → `register.ps1` 这条完整链路真正跑通过一次**，
  而且是真的提权成功：跑完后 HKLM 下的 CLSID / InprocServer32 / LanguageProfile /
  4 个 Category 双份键全部真实存在且回读一致，`C:\Program Files\XingbianIME\` 下
  三个文件也都落盘了，`Get-WinUserLanguageList` 里多出了本输入法的 TIP
  （说明 `InstallLayoutOrTip` 那一步也真的生效了）
- 非管理员跑默认 HKLM 路线确实以**退出码 2** 结束并打印「请以管理员身份运行」
- **注册表结构用「假注册表」做过逐项断言**：把脚本里的 `[Microsoft.Win32.Registry]::LocalMachine
  / ::CurrentUser` 重定向到一个内存假注册表后真跑一遍（只改临时副本，不改交付文件），
  再对导出的键值做断言 —— 注册方向 **30 项全过**（键层级、默认值、
  `Enable`/`IconIndex` 是 `DWORD`、4 个 Category 叶子必须**无值**、
  被禁的 COMLESS / SECUREMODE 必须**不存在**、别人的 TIP 必须**不被碰**），
  卸载方向 **17 项全过**（我们的项全没了，别人的项全还在）

**未实测**
- **真实宿主程序（记事本 / 浏览器）里的端到端输入**：装机本身成功了，
  但没有在真实宿主的文档里敲过 `nima` 验证候选窗与上屏
- `-PerUser`（HKCU）路线：只验证了它会写、结构对，没验证 TSF 认不认
- **卸载链路没有真实跑过**：`unregister.ps1` 的语法、退出码、注册表路径都验证过，
  但没在真机上执行过一次完整卸载（那会改动系统状态）
- `ILOT_UNINSTALL` 的值取自多处公开 SDK 转写（corvusskk、Vanara、rime/weasel、
  VietType 四处一致为 `0x00000001`），**不是**从本机 Windows SDK 头文件读到的
  —— 本机没装 Windows SDK，且 `input.h` 在可达的公开镜像里都取不到

> 注：`install.cmd` 那次真实运行暴露过一个缺陷 —— `register.ps1` 第 5 步写
> `HKCU` 用户级启用位时，如果写入抛异常，原来只 `Write-Warn` 就继续走到 `exit 0`，
> 于是出现「HKCU 启用位其实没写成、脚本却报告安装成功」。
> 现已改为记入回读错误列表并按**退出码 4** 失败。

---

## 10. 相关文档

- `docs\FORMAT.md` —— 词典 `xingbian.bin` 的二进制格式契约（v1）
- `tools\build_dict.py` —— 数据管线；`--selftest` 有 16 项自检
- `src\Xingbian.Cli\` —— 命令行工具，可以脱离输入法直接试词典：
  `Xingbian.Cli.exe --transform 你妈` / `--input nima` / `--selftest` / `--bench`
