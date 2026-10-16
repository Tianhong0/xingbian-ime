#Requires -Version 5.1
<#
.SYNOPSIS
    形变输入法 (xingbian-ime) 注册 / 安装脚本 —— Windows PowerShell 5.1

.DESCRIPTION
    把已经编译好的 TSF 文本服务（TIP，托管 in-proc COM 服务器）注册进 Windows 输入法框架：

      1. 复制 Xingbian.Tsf.dll + Xingbian.Core.dll 到安装目录根，词典复制到 <安装目录>\dict\xingbian.bin
         （Xingbian.Core.dll 必须和 Xingbian.Tsf.dll 在同一目录，否则 CLR 加载 TextService 会失败）
      2. 写 HKLM\SOFTWARE\Classes\CLSID\{CLSID}\ 及 \InprocServer32\（mscoree.dll + 托管注册信息）
      3. 写 HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\LanguageProfile\0x00000804\{ProfileGUID}\
      4. 写 HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\Category\Category\... 与 \Category\Item\...（4 个 Category）
      5. 写 HKCU 的用户级启用位 Enable=1
      6. 调用 input.dll!InstallLayoutOrTip 把输入法加入语言列表（失败只警告，用户可以手工添加）

    每一处注册表写入都会在写完后立刻回读校验，任何一项不符都会报错并列出是哪一项。

.PARAMETER PerUser
    【实验路径，本机未验证】把本应写进 HKLM 的两棵子树（Software\Classes\CLSID\{CLSID} 和
    SOFTWARE\Microsoft\CTF\TIP\{CLSID}）原样写进 HKCU，从而不需要管理员权限。
    这条路径是否会被 TSF 采纳尚未验证：如果装完后 Win+空格 里看不到「形变输入法」，
    请改用默认（不带本开关）的 HKLM 路线。

.PARAMETER InstallDir
    安装目录。默认 C:\Program Files\XingbianIME

.PARAMETER Source
    构建产物目录（含 Xingbian.Tsf.dll 与 Xingbian.Core.dll）。
    默认：脚本所在目录\..\src\Xingbian.Tsf\bin\Release

.PARAMETER Dict
    词典文件路径。默认：脚本所在目录\..\assets\build\xingbian.bin

.PARAMETER SkipCopy
    只写注册表，不复制任何文件（安装目录里应该已经有 dll 和词典）。

.PARAMETER Quiet
    安静模式：只输出警告和错误，成功时不打印任何内容。

.EXITCODE
    0 = 成功
    2 = 权限不足（HKLM 路线需要管理员）
    3 = 源文件缺失
    4 = 注册表写入 / 回读校验失败

.EXAMPLE
    .\register.ps1
    默认路线：复制文件到 C:\Program Files\XingbianIME 并写 HKLM（需要管理员）。

.EXAMPLE
    .\register.ps1 -PerUser
    实验路线：写 HKCU，不需要管理员，但 TSF 是否认这条路径未验证。

.EXAMPLE
    .\register.ps1 -SkipCopy -InstallDir 'D:\ime'
    只在 D:\ime 已经放好文件的情况下写注册表。
#>
[CmdletBinding()]
param(
    [switch]$PerUser,
    [string]$InstallDir = 'C:\Program Files\XingbianIME',
    [string]$Source,
    [string]$Dict,
    [switch]$SkipCopy,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 冻结标识符（权威源：src\Xingbian.Tsf\XingbianTsfIds.cs 与 src\Xingbian.Tsf\Interop\TsfInterop.cs）
# 这些值必须逐字节一致，改这里之前先改那边的源码。
# ---------------------------------------------------------------------------
$Clsid          = '{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}'   # XingbianTsfIds.Clsid
$ProfileGuid    = '{B3F2C8D1-6A47-4C09-8E52-1D7A9F0B4C65}'   # XingbianTsfIds.ProfileGuid
$LangIdKeyName  = '0x00000804'                                # XingbianTsfIds.LangId = 0x0804，键名形式 0x + X8
$LangIdPsz      = '0804'                                      # InstallLayoutOrTip 用的 LANGID 文本
$ClassFullName  = 'Xingbian.Tsf.TextService'                  # XingbianTsfIds.ClassFullName
$RuntimeVersion = 'v4.0.30319'                                # XingbianTsfIds.RuntimeVersion
$Description    = '形变输入法'                                 # XingbianTsfIds.Description
$TsfDllName     = 'Xingbian.Tsf.dll'                          # XingbianTsfIds.DllName
$CoreDllName    = 'Xingbian.Core.dll'
$ManagedServer  = 'C:\Windows\System32\mscoree.dll'
$AssemblyFallback = 'Xingbian.Tsf, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null'

# 需要注册的 4 个 Category（GUID 来自 src\Xingbian.Tsf\Interop\TsfInterop.cs 第 96-105 行）
$CategoryGuids = @(
    '{34745C63-B2F0-4784-8B67-5E12C8701A31}',   # GUID_TFCAT_TIP_KEYBOARD          (TsfInterop.cs:96)
    '{49D2F9CE-1F5E-11D7-A6D3-00065B84435C}',   # GUID_TFCAT_TIPCAP_UIELEMENTENABLED (TsfInterop.cs:99)
    '{13A016DF-560B-46CD-947A-4C3AF1E0E35D}',   # GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT (TsfInterop.cs:102)
    '{25504FB4-7BAB-4BC1-9C69-CF81890F0EF5}'    # GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT   (TsfInterop.cs:105)
)
# 注意：不注册 GUID_TFCAT_TIPCAP_COMLESS 与 GUID_TFCAT_TIPCAP_SECUREMODE。

# input.h（Windows SDK um\input.h）中的标志位，见 unregister.ps1 里对 ILOT_UNINSTALL 的出处说明
$ILOT_INSTALL = [uint32]0x00000000

$ExitSuccess       = 0
$ExitNotAdmin      = 2
$ExitMissingSource = 3
$ExitRegistry      = 4

# ---------------------------------------------------------------------------
# 参数默认值（相对脚本位置）
# ---------------------------------------------------------------------------
if (-not $PSBoundParameters.ContainsKey('Source')) {
    $Source = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Xingbian.Tsf\bin\Release'))
}
if (-not $PSBoundParameters.ContainsKey('Dict')) {
    $Dict = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\assets\build\xingbian.bin'))
}
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)

# ---------------------------------------------------------------------------
# 输出helper
# ---------------------------------------------------------------------------
function Write-Info {
    param([string]$Message)
    if (-not $Quiet) { Write-Host $Message }
}
function Write-Step {
    param([string]$Message)
    if (-not $Quiet) { Write-Host ''; Write-Host ("== " + $Message) -ForegroundColor Cyan }
}
function Write-Warn {
    param([string]$Message)
    Write-Host ("[警告] " + $Message) -ForegroundColor Yellow
}
function Write-Err {
    param([string]$Message)
    Write-Host ("[错误] " + $Message) -ForegroundColor Red
}

# ---------------------------------------------------------------------------
# 注册表：统一走 [Microsoft.Win32.Registry] 的 .NET API（不用 New-Item -Force）
# 本机教训：New-Item -Force 这类写法曾静默吞掉「访问被拒绝」，导致误判成功。
# ---------------------------------------------------------------------------
$script:IsPerUser = [bool]$PerUser
if ($script:IsPerUser) {
    $script:RegRoot  = [Microsoft.Win32.Registry]::CurrentUser
    $script:RootLabel = 'HKCU'
    $script:ClassesClsidRoot = 'Software\Classes\CLSID'
    $script:CtfTipRoot       = 'Software\Microsoft\CTF\TIP'
} else {
    $script:RegRoot  = [Microsoft.Win32.Registry]::LocalMachine
    $script:RootLabel = 'HKLM'
    $script:ClassesClsidRoot = 'SOFTWARE\Classes\CLSID'
    $script:CtfTipRoot       = 'SOFTWARE\Microsoft\CTF\TIP'
}
$script:ReadbackErrors = New-Object System.Collections.ArrayList

function Test-AccessDenied {
    param([System.Exception]$Exception)
    $e = $Exception
    while ($null -ne $e) {
        if ($e -is [System.UnauthorizedAccessException] -or $e -is [System.Security.SecurityException]) { return $true }
        $e = $e.InnerException
    }
    return $false
}

function Add-ReadbackError {
    param([string]$Item, [string]$Detail)
    [void]$script:ReadbackErrors.Add(($Item + ' -> ' + $Detail))
}

function Set-RegValue {
    param(
        [Parameter(Mandatory = $true)][string]$SubKey,
        [string]$Name = '',
        [Parameter(Mandatory = $true)][AllowNull()][object]$Value,
        [Microsoft.Win32.RegistryValueKind]$Kind = [Microsoft.Win32.RegistryValueKind]::String
    )
    $fullPath = $script:RootLabel + '\' + $SubKey
    if ($Name -eq '') { $display = $fullPath + '  (默认值)' } else { $display = $fullPath + '  [' + $Name + ']' }

    $key = $script:RegRoot.CreateSubKey($SubKey, $true)
    if ($null -eq $key) { throw ('创建注册表项失败（CreateSubKey 返回 null）: ' + $fullPath) }
    try {
        $key.SetValue($Name, $Value, $Kind)
    } finally {
        $key.Dispose()
    }

    # 写完立刻回读校验
    $read = $script:RegRoot.OpenSubKey($SubKey, $false)
    if ($null -eq $read) {
        Add-ReadbackError $display '回读时该项不存在'
        return
    }
    try {
        $names = @($read.GetValueNames())
        if ($names -notcontains $Name) {
            Add-ReadbackError $display '回读时该值不存在'
            return
        }
        $actual     = $read.GetValue($Name)
        $actualKind = $read.GetValueKind($Name)
    } finally {
        $read.Dispose()
    }

    if ($actualKind -ne $Kind) {
        Add-ReadbackError $display ('类型不符：期望 ' + $Kind + '，实际 ' + $actualKind)
        return
    }
    if ([string]$actual -cne [string]$Value) {
        Add-ReadbackError $display ("值不符：期望 '" + [string]$Value + "'，实际 '" + [string]$actual + "'")
        return
    }
    Write-Info ('  [OK] ' + $display + ' = ' + [string]$Value)
}

function New-RegKey {
    param([Parameter(Mandatory = $true)][string]$SubKey)
    $fullPath = $script:RootLabel + '\' + $SubKey
    $key = $script:RegRoot.CreateSubKey($SubKey, $true)
    if ($null -eq $key) { throw ('创建注册表项失败（CreateSubKey 返回 null）: ' + $fullPath) }
    $key.Dispose()

    $read = $script:RegRoot.OpenSubKey($SubKey, $false)
    if ($null -eq $read) {
        Add-ReadbackError $fullPath '回读时该项不存在'
        return
    }
    $read.Dispose()
    Write-Info ('  [OK] ' + $fullPath + '   (空键)')
}

function Test-RegKeyExists {
    param([Parameter(Mandatory = $true)][string]$SubKey)
    $key = $script:RegRoot.OpenSubKey($SubKey, $false)
    if ($null -eq $key) { return $false }
    $key.Dispose()
    return $true
}

function Remove-RegTree {
    param([Parameter(Mandatory = $true)][string]$SubKey)
    $fullPath = $script:RootLabel + '\' + $SubKey
    try {
        $script:RegRoot.DeleteSubKeyTree($SubKey, $false)
    } catch {
        throw ('删除注册表项失败: ' + $fullPath + ' : ' + $_.Exception.Message)
    }
    if (Test-RegKeyExists $SubKey) { throw ('删除后该项仍然存在: ' + $fullPath) }
    Write-Info ('  [清理] ' + $fullPath)
}

function Get-TsfAssemblyFullName {
    $candidates = @(
        (Join-Path $InstallDir $TsfDllName),
        (Join-Path $Source $TsfDllName)
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            try {
                return ([System.Reflection.AssemblyName]::GetAssemblyName($candidate)).FullName
            } catch {
                Write-Warn ('无法从 ' + $candidate + ' 读取程序集标识：' + $_.Exception.Message)
            }
        }
    }
    return $null
}

# ---------------------------------------------------------------------------
# input.dll!InstallLayoutOrTip —— 把输入法加进语言列表，省得用户手工去「添加键盘」
# ---------------------------------------------------------------------------
function Invoke-InstallLayoutOrTip {
    param(
        [Parameter(Mandatory = $true)][string]$Psz,
        [Parameter(Mandatory = $true)][uint32]$Flags
    )
    $memberDefinition = @'
[System.Runtime.InteropServices.DllImport("input.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern bool InstallLayoutOrTip(string psz, uint dwFlags);
'@
    if (-not ('XingbianInstall.NativeInput' -as [type])) {
        Add-Type -Namespace XingbianInstall -Name NativeInput -MemberDefinition $memberDefinition -ErrorAction Stop
    }
    return [XingbianInstall.NativeInput]::InstallLayoutOrTip($Psz, $Flags)
}

$TipPsz = $LangIdPsz + ':' + $Clsid + $ProfileGuid

# ===========================================================================
# 0. 环境与前置检查
# ===========================================================================
Write-Host ''
Write-Host '形变输入法 (xingbian-ime) 注册脚本' -ForegroundColor Green
Write-Info ('  路线        : ' + $(if ($script:IsPerUser) { 'HKCU（-PerUser 实验路径，未验证）' } else { 'HKLM（默认，需要管理员）' }))
Write-Info ('  安装目录    : ' + $InstallDir)
Write-Info ('  构建产物目录: ' + $Source)
Write-Info ('  词典        : ' + $Dict)
Write-Info ('  CLSID       : ' + $Clsid)
Write-Info ('  ProfileGUID : ' + $ProfileGuid)
Write-Info ('  语言配置键  : ' + $LangIdKeyName)
Write-Info ('  语言列表串  : ' + $TipPsz)

if ($script:IsPerUser) {
    Write-Host ''
    Write-Host '  *** 注意：-PerUser 是【未经验证的实验路径】 ***' -ForegroundColor Yellow
    Write-Host '  它把本应写进 HKLM 的两棵子树写进 HKCU\Software\Classes\CLSID\{CLSID} 和' -ForegroundColor Yellow
    Write-Host '  HKCU\SOFTWARE\Microsoft\CTF\TIP\{CLSID}。TSF 是否会采纳 HKCU 下的 CLSID 子树，' -ForegroundColor Yellow
    Write-Host '  在本机没有验证过。如果 Win+空格 里看不到「形变输入法」，请改用默认路线：' -ForegroundColor Yellow
    Write-Host '      右键以管理员身份运行 install.cmd      （即不带 -PerUser）' -ForegroundColor Yellow
    Write-Host ''
}

# 32 位 PowerShell 会把注册写进 WOW6432Node，64 位 TSF 永远读不到 —— 直接拦下
if (-not [Environment]::Is64BitProcess) {
    Write-Err '当前是 32 位 PowerShell。本输入法是 x64 的 TSF 服务，必须用 64 位 PowerShell 注册。'
    Write-Err '请改用 C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe，或直接运行 install.cmd。'
    exit $ExitRegistry
}

# HKLM 路线必须是管理员
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $script:IsPerUser -and -not $isAdmin) {
    Write-Err '权限不足：写入 HKLM 需要管理员权限，当前进程不是管理员。'
    Write-Err '请右键点击 install.cmd，选择「以管理员身份运行」；'
    Write-Err '或者右键「Windows PowerShell」→「以管理员身份运行」，再执行本脚本。'
    exit $ExitNotAdmin
}
if (-not $isAdmin) {
    Write-Warn '当前不是管理员：只能写 HKCU。如果 HKLM 下残留了旧的注册项，将无法清理。'
}

# 源文件检查（-SkipCopy 时跳过）
if (-not $SkipCopy) {
    $missing = New-Object System.Collections.ArrayList
    foreach ($name in @($TsfDllName, $CoreDllName)) {
        $p = Join-Path $Source $name
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { [void]$missing.Add($p) }
    }
    if (-not (Test-Path -LiteralPath $Dict -PathType Leaf)) { [void]$missing.Add($Dict) }
    if ($missing.Count -gt 0) {
        Write-Err '源文件缺失，安装中止：'
        foreach ($m in $missing) { Write-Err ('    找不到: ' + $m) }
        Write-Err ('请先编译项目（Release），或用 -Source / -Dict 指定正确路径。')
        Write-Err ('注意 ' + $CoreDllName + ' 必须和 ' + $TsfDllName + ' 一起复制到同一个目录，否则 CLR 加载 TextService 会失败。')
        exit $ExitMissingSource
    }
}

# ===========================================================================
# 1. 复制文件
# ===========================================================================
if ($SkipCopy) {
    Write-Step '1/5 复制文件 —— 已指定 -SkipCopy，跳过'
} else {
    Write-Step '1/5 复制文件'
    try {
        if (-not (Test-Path -LiteralPath $InstallDir)) {
            [void](New-Item -ItemType Directory -Path $InstallDir -Force -ErrorAction Stop)
        }
        $dictDir = Join-Path $InstallDir 'dict'
        if (-not (Test-Path -LiteralPath $dictDir)) {
            [void](New-Item -ItemType Directory -Path $dictDir -Force -ErrorAction Stop)
        }
        foreach ($name in @($TsfDllName, $CoreDllName)) {
            $from = Join-Path $Source $name
            $to   = Join-Path $InstallDir $name
            Copy-Item -LiteralPath $from -Destination $to -Force -ErrorAction Stop
            Write-Info ('  [OK] ' + $from + '  ->  ' + $to)
        }
        $dictTo = Join-Path $dictDir 'xingbian.bin'
        Copy-Item -LiteralPath $Dict -Destination $dictTo -Force -ErrorAction Stop
        Write-Info ('  [OK] ' + $Dict + '  ->  ' + $dictTo)

        foreach ($p in @((Join-Path $InstallDir $TsfDllName), (Join-Path $InstallDir $CoreDllName), $dictTo)) {
            if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw ('复制后文件不存在: ' + $p) }
        }
    } catch {
        if (Test-AccessDenied $_.Exception) {
            Write-Err ('复制文件失败：权限不足，请以管理员身份运行。（' + $_.Exception.Message + '）')
            exit $ExitNotAdmin
        }
        Write-Err ('复制文件失败：' + $_.Exception.Message)
        exit $ExitRegistry
    }
}

# 程序集标识：优先从真实 dll 读，读不到才退回常量
$assemblyFullName = Get-TsfAssemblyFullName
if ([string]::IsNullOrEmpty($assemblyFullName)) {
    $assemblyFullName = $AssemblyFallback
    Write-Warn ('无法读取 dll 的程序集标识，改用常量：' + $AssemblyFallback)
} elseif ($assemblyFullName -cne $AssemblyFallback) {
    Write-Warn ('程序集标识与脚本常量不一致，将按 dll 实际值注册：' + $assemblyFullName)
}

# CodeBase：file:///C:/Program Files/XingbianIME/Xingbian.Tsf.dll
$codeBase = 'file:///' + ($InstallDir -replace '\\', '/') + '/' + $TsfDllName
$iconFile = Join-Path $InstallDir $TsfDllName

# ===========================================================================
# 2-4. 注册表
# ===========================================================================
try {
    # ---- 删除旧注册（幂等，可重复运行）----
    Write-Step '2/5 清理旧的 CLSID / TIP 注册'
    Remove-RegTree ($script:ClassesClsidRoot + '\' + $Clsid)
    Remove-RegTree ($script:CtfTipRoot + '\' + $Clsid)
    if (-not $script:IsPerUser) {
        # 之前若跑过 -PerUser，HKCU\Software\Classes 的注册会遮蔽 HKLM（HKCU 优先级更高），一并清掉
        $hkcuClasses = 'HKCU:\Software\Classes\CLSID\' + $Clsid
        if (Test-Path -LiteralPath $hkcuClasses) {
            Write-Info ('  [清理] HKCU\Software\Classes\CLSID\' + $Clsid + '  (旧 -PerUser 残留)')
            Remove-Item -LiteralPath $hkcuClasses -Recurse -Force -ErrorAction Stop
        }
    }

    # ---- 3. CLSID / InprocServer32 ----
    Write-Step '3/5 写 COM 注册（CLSID + InprocServer32）'
    $clsidKey = $script:ClassesClsidRoot + '\' + $Clsid
    Set-RegValue -SubKey $clsidKey -Name '' -Value $Description
    $inprocKey = $clsidKey + '\InprocServer32'
    Set-RegValue -SubKey $inprocKey -Name ''                  -Value $ManagedServer
    Set-RegValue -SubKey $inprocKey -Name 'ThreadingModel'    -Value 'Both'
    Set-RegValue -SubKey $inprocKey -Name 'Assembly'          -Value $assemblyFullName
    Set-RegValue -SubKey $inprocKey -Name 'Class'             -Value $ClassFullName
    Set-RegValue -SubKey $inprocKey -Name 'RuntimeVersion'    -Value $RuntimeVersion
    Set-RegValue -SubKey $inprocKey -Name 'CodeBase'          -Value $codeBase

    # ---- 4. CTF TIP：语言配置 + Category ----
    Write-Step '4/5 写 TSF 注册（LanguageProfile + 4 个 Category）'
    $tipKey = $script:CtfTipRoot + '\' + $Clsid
    $profileKey = $tipKey + '\LanguageProfile\' + $LangIdKeyName + '\' + $ProfileGuid
    # 语言配置项的默认值 = 用户可见的输入法名称。
    # 本机微软拼音这类系统自带 TIP 只写具名值 Description / Display Description，
    # 并没有默认值；TSF 自己也不读这个默认值。这里按需求写一份，
    # 真正让 Win+空格 显示「形变输入法」的是下面那两行具名值。
    Set-RegValue -SubKey $profileKey -Name ''                    -Value $Description
    Set-RegValue -SubKey $profileKey -Name 'Description'         -Value $Description
    Set-RegValue -SubKey $profileKey -Name 'Display Description' -Value $Description
    Set-RegValue -SubKey $profileKey -Name 'IconFile'            -Value $iconFile
    Set-RegValue -SubKey $profileKey -Name 'IconIndex'           -Value ([int]0)     -Kind ([Microsoft.Win32.RegistryValueKind]::DWord)
    Set-RegValue -SubKey $profileKey -Name 'Enable'              -Value ([int]1)     -Kind ([Microsoft.Win32.RegistryValueKind]::DWord)

    foreach ($cat in $CategoryGuids) {
        $catLeaf = $tipKey + '\Category\Category\' + $cat + '\' + $Clsid
        New-RegKey -SubKey $catLeaf
        $itemLeaf = $tipKey + '\Category\Item\' + $Clsid + '\' + $cat
        New-RegKey -SubKey $itemLeaf
        if (-not (Test-RegKeyExists $catLeaf))  { Add-ReadbackError ($script:RootLabel + '\' + $catLeaf)  '回读时该项不存在' }
        if (-not (Test-RegKeyExists $itemLeaf)) { Add-ReadbackError ($script:RootLabel + '\' + $itemLeaf) '回读时该项不存在' }
    }
} catch {
    if (Test-AccessDenied $_.Exception) {
        Write-Err ('注册表写入被拒绝：权限不足，请以管理员身份运行。（' + $_.Exception.Message + '）')
        exit $ExitNotAdmin
    }
    Write-Err ('注册表写入失败：' + $_.Exception.Message)
    exit $ExitRegistry
}

# ---- 5. HKCU 用户级项（不需要管理员）：启用位 + 词典路径 ----
Write-Step '5/5 写 HKCU 用户级项（启用位 + 词典路径）'
# 这些标签在 try 外面算好，供 catch 里报错用（异常可能发生在赋值之前）
$hkcuProfileLabel = 'HKCU\Software\Microsoft\CTF\TIP\' + $Clsid + '\LanguageProfile\' + $LangIdKeyName + '\' + $ProfileGuid
$hkcuDictLabel    = 'HKCU\Software\XingbianIME  [DictPath]'
$hkcuStageLabel   = $hkcuProfileLabel + '  [Enable]'
try {
    $hkcuTipRoot = 'Software\Microsoft\CTF\TIP'
    if ($script:IsPerUser) {
        Write-Info '  （-PerUser 路线下这一项已经在上面写过，无需重复）'
    } else {
        $hkcuProfile = $hkcuTipRoot + '\' + $Clsid + '\LanguageProfile\' + $LangIdKeyName + '\' + $ProfileGuid
        $hkcuKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($hkcuProfile, $true)
        if ($null -eq $hkcuKey) { throw ('创建注册表项失败（CreateSubKey 返回 null）: HKCU\' + $hkcuProfile) }
        try { $hkcuKey.SetValue('Enable', [int]1, [Microsoft.Win32.RegistryValueKind]::DWord) } finally { $hkcuKey.Dispose() }

        $read = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($hkcuProfile, $false)
        if ($null -eq $read) {
            Add-ReadbackError ('HKCU\' + $hkcuProfile + '  [Enable]') '回读时该项不存在'
        } else {
            try {
                if (@($read.GetValueNames()) -notcontains 'Enable') {
                    Add-ReadbackError ('HKCU\' + $hkcuProfile + '  [Enable]') '回读时该值不存在'
                } elseif ([int]$read.GetValue('Enable') -ne 1) {
                    Add-ReadbackError ('HKCU\' + $hkcuProfile + '  [Enable]') ('值不符：期望 1，实际 ' + [string]$read.GetValue('Enable'))
                } else {
                    Write-Info ('  [OK] HKCU\' + $hkcuProfile + '  [Enable] = 1')
                }
            } finally { $read.Dispose() }
        }
    }

    # ---- 词典路径（双保险）----
    # XingbianEngine.CreateDefault() 的查找顺序：
    #   1) 环境变量 XINGBIAN_DICT
    #   2) HKCU\Software\XingbianIME  [DictPath]   <- 就是这一项
    #   3) 程序集所在目录\dict\xingbian.bin
    #   4) 程序集所在目录\xingbian.bin
    # 第 3 条本来就够用，这里再写一份第 2 条，等于双保险：
    # 以后哪怕有人把 dll 挪到别处，只要词典还在这台机器上就仍能找到。
    #
    # 注意：HKCU 是【当前登录用户】的配置。UAC 提权不换用户，所以提权后写到的
    # 仍然是同一个人；但若把本脚本用于给【别的用户】安装，这里写的是执行脚本的
    # 那个管理员自己的 HKCU，那位最终用户读不到，只能靠第 3 条兜底。
    $dictTargetPath = Join-Path (Join-Path $InstallDir 'dict') 'xingbian.bin'
    $dictSubKey = 'Software\XingbianIME'
    $dictKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($dictSubKey, $true)
    if ($null -eq $dictKey) { throw ('创建注册表项失败（CreateSubKey 返回 null）: HKCU\' + $dictSubKey) }
    try { $dictKey.SetValue('DictPath', $dictTargetPath, [Microsoft.Win32.RegistryValueKind]::String) } finally { $dictKey.Dispose() }

    $dictRead = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($dictSubKey, $false)
    if ($null -eq $dictRead) {
        Add-ReadbackError $hkcuDictLabel '回读时该项不存在'
    } else {
        try {
            $dictNames = @($dictRead.GetValueNames())
            if ($dictNames -notcontains 'DictPath') {
                Add-ReadbackError $hkcuDictLabel '回读时该值不存在'
            } elseif ($dictRead.GetValueKind('DictPath') -ne [Microsoft.Win32.RegistryValueKind]::String) {
                Add-ReadbackError $hkcuDictLabel ('类型不符：期望 String，实际 ' + [string]$dictRead.GetValueKind('DictPath'))
            } elseif ([string]$dictRead.GetValue('DictPath') -cne $dictTargetPath) {
                Add-ReadbackError $hkcuDictLabel ("值不符：期望 '" + $dictTargetPath + "'，实际 '" + [string]$dictRead.GetValue('DictPath') + "'")
            } else {
                Write-Info ('  [OK] HKCU\' + $dictSubKey + '  [DictPath] = ' + $dictTargetPath)
            }
        } finally { $dictRead.Dispose() }
    }
} catch {
    # 重要：原来这里只 Write-Warn 然后继续走到 exit 0，结果是「HKCU 启用位没写成、
    # 但脚本报告安装成功」—— 正是本脚本开头警告过的那种静默失败。
    # 实测证据：在某次真实提权安装中，HKCU\SOFTWARE\Microsoft\CTF\TIP\{CLSID}
    # 最终并不存在，而那次运行仍然打印了「[成功] 形变输入法已经注册进系统」。
    # 因此这里改为记入回读错误列表，最终按退出码 4 失败。
    if (Test-AccessDenied $_.Exception) {
        Write-Err ('HKCU 用户级项写入失败（权限不足）：' + $_.Exception.Message)
    } else {
        Write-Err ('HKCU 用户级项写入失败：' + $_.Exception.Message)
    }
    Add-ReadbackError $hkcuStageLabel ('写入抛出异常：' + $_.Exception.Message)
}

# ---- 回读校验汇总 ----
if ($script:ReadbackErrors.Count -gt 0) {
    Write-Host ''
    Write-Err ('注册表回读校验失败，共 ' + $script:ReadbackErrors.Count + ' 项：')
    foreach ($e in $script:ReadbackErrors) { Write-Err ('    ' + $e) }
    Write-Err '注册视为失败。请确认以管理员身份运行；如仍失败，可参照 install\README.md 手工添加注册表项。'
    exit $ExitRegistry
}
Write-Info ''
Write-Info '所有注册表项均已回读校验通过。'

# ===========================================================================
# 6. 把输入法加进语言列表
# ===========================================================================
try {
    $ok = Invoke-InstallLayoutOrTip -Psz $TipPsz -Flags $ILOT_INSTALL
    if ($ok) {
        Write-Info ('  [OK] input.dll!InstallLayoutOrTip 成功：' + $TipPsz + '  (flags=ILOT_INSTALL=0x00000000)')
    } else {
        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Write-Warn ('input.dll!InstallLayoutOrTip 返回失败（Win32 错误码 ' + $err + '）。注册表已写好，输入法应该仍可用，')
        Write-Warn '只是在语言列表里可能不出现，需要手工添加：设置 → 时间和语言 → 语言 → 中文(简体) → 选项 → 添加键盘。'
    }
} catch {
    Write-Warn ('调用 InstallLayoutOrTip 失败：' + $_.Exception.Message)
    Write-Warn '这不影响注册表注册，可手工添加键盘：设置 → 时间和语言 → 语言 → 中文(简体) → 选项 → 添加键盘。'
}

# ===========================================================================
# 完成
# ===========================================================================
if (-not $Quiet) {
    Write-Host ''
    Write-Host '========================================' -ForegroundColor Green
    Write-Host ' 形变输入法注册完成' -ForegroundColor Green
    Write-Host '========================================' -ForegroundColor Green
    Write-Host ''
    Write-Host '接下来请手动做这几步：'
    Write-Host ''
    Write-Host '  1) 让 TSF 重新加载（二选一）'
    Write-Host '     · 最稳妥：注销并重新登录（或重启）'
    Write-Host '     · 快一点：重启 ctfmon.exe（系统会自动把它拉起来），在 PowerShell 里执行'
    Write-Host '           Stop-Process -Name ctfmon -Force -ErrorAction SilentlyContinue'
    Write-Host ''
    Write-Host '  2) 按 Win + 空格 切换输入法，列表里应该能看到「形变输入法」'
    Write-Host ''
    Write-Host '  3) 打开记事本，敲 nima，应弹出候选窗；按空格上屏得到：亻尔 女马'
    Write-Host ''
    Write-Host '  中英文切换：单击 Shift'
    Write-Host ''
    if ($script:IsPerUser) {
        Write-Host '  再次提醒：-PerUser 是未验证的实验路线。若 Win+空格 里看不到「形变输入法」，' -ForegroundColor Yellow
        Write-Host '  请右键以管理员身份运行 install.cmd（走默认 HKLM 路线）。' -ForegroundColor Yellow
        Write-Host ''
    }
    Write-Host '  卸载：右键以管理员身份运行 uninstall.cmd'
    Write-Host ''
}

exit $ExitSuccess
