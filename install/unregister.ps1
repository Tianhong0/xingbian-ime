#Requires -Version 5.1
<#
.SYNOPSIS
    形变输入法 (xingbian-ime) 反注册 / 卸载脚本 —— Windows PowerShell 5.1

.DESCRIPTION
    把注册过的 TSF 文本服务从系统里摘掉：

      1. 先调用 input.dll!InstallLayoutOrTip(psz, ILOT_UNINSTALL) 把输入法从语言列表移除
         （必须在删注册表【之前】调用，否则 psz 解析不到了）
      2. 删除 HKCU\SOFTWARE\Microsoft\CTF\TIP\{CLSID} 与 HKCU\Software\Classes\CLSID\{CLSID}
      3. 删除 HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID} 与 HKLM\SOFTWARE\Classes\CLSID\{CLSID}
      4. 仅当指定 -RemoveFiles 时，才删除安装目录

    两条路线都会尝试清理（幂等，项不存在不算错误），这样可以同时干掉
    以前用 -PerUser 装出来的残留。

.PARAMETER PerUser
    表示该输入法是以 -PerUser（HKCU）方式注册的。主要影响提示文字的措辞；
    实际清理时 HKCU 与 HKLM 两处都会尝试。

.PARAMETER InstallDir
    安装目录。默认 C:\Program Files\XingbianIME

.PARAMETER RemoveFiles
    同时删除安装目录及其中的文件。默认【保留】，方便排障。

.PARAMETER Quiet
    安静模式：只输出警告和错误。

.EXITCODE
    0 = 成功
    2 = 权限不足（删除 HKLM 下的项需要管理员）
    3 = 源文件缺失（本脚本不读源文件，不会返回此码）
    4 = 注册表删除 / 回读校验失败

.EXAMPLE
    .\unregister.ps1
    删除注册，保留 C:\Program Files\XingbianIME 下的文件。

.EXAMPLE
    .\unregister.ps1 -RemoveFiles
    连安装目录一起删掉。

.NOTES
    ILOT_UNINSTALL = 0x00000001
    出处：Windows SDK 的 um\input.h 中 InstallLayoutOrTip 系列函数的标志位定义
          （本地无 Windows Kits，故核对了多处公开发布的 SDK 头文件转写与实现，四者一致：
           nathancorvussolis/corvusskk  libinput/input.h:46
               #define ILOT_UNINSTALL               0x00000001 // Same as ILOT_DISABLED.
           完整定义块（同文件 45-53 行）：
               #define ILOT_INSTALL                 0x00000000
               #define ILOT_UNINSTALL               0x00000001 // Same as ILOT_DISABLED.
               #define ILOT_DEFPROFILE              0x00000002
               #define ILOT_DEFUSER4                0x00000004
               #define ILOT_SYSLOCALE               0x00000008
               #define ILOT_NOLOCALETOENUMERATE     0x00000010
               #define ILOT_NOAPPLYTOCURRENTSESSION 0x00000020
               #define ILOT_CLEANINSTALL            0x00000040
               #define ILOT_DISABLED                0x00000080
           dahall/Vanara  PInvoke/TextServicesFramework/msctf.Funcs.cs 的 ILOT 枚举同样为
               ILOT_UNINSTALL = 0x00000001
           另有 Windows 上的实际实现佐证：rime/weasel 的 WeaselSetup/imesetup.cpp、
           dinhngtu/VietType 的 VietTypeATL/Register.cpp（static constexpr DWORD
           ILOT_UNINSTALL = 0x00000001;）均使用该值。
#>
[CmdletBinding()]
param(
    [switch]$PerUser,
    [string]$InstallDir = 'C:\Program Files\XingbianIME',
    [switch]$RemoveFiles,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 冻结标识符（权威源：src\Xingbian.Tsf\XingbianTsfIds.cs）
# ---------------------------------------------------------------------------
$Clsid         = '{7C4E1A9B-3D2F-4E58-9A61-0F8B5C7D2E43}'   # XingbianTsfIds.Clsid
$ProfileGuid   = '{B3F2C8D1-6A47-4C09-8E52-1D7A9F0B4C65}'   # XingbianTsfIds.ProfileGuid
$LangIdKeyName = '0x00000804'                                # XingbianTsfIds.LangId = 0x0804
$LangIdPsz     = '0804'

# ILOT_UNINSTALL：见文件头 .NOTES 的出处说明
$ILOT_UNINSTALL = [uint32]0x00000001

$ExitSuccess       = 0
$ExitNotAdmin      = 2
$ExitMissingSource = 3
$ExitRegistry      = 4

$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)

# ---------------------------------------------------------------------------
# 输出 helper
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

function Test-AccessDenied {
    param([System.Exception]$Exception)
    $e = $Exception
    while ($null -ne $e) {
        if ($e -is [System.UnauthorizedAccessException] -or $e -is [System.Security.SecurityException]) { return $true }
        $e = $e.InnerException
    }
    return $false
}

$script:DeleteErrors = New-Object System.Collections.ArrayList

function Remove-RegTreeAt {
    param(
        [Parameter(Mandatory = $true)]$Hive,
        [Parameter(Mandatory = $true)][string]$HiveLabel,
        [Parameter(Mandatory = $true)][string]$SubKey
    )
    $fullPath = $HiveLabel + '\' + $SubKey
    # 幂等：不存在就直接返回
    $probe = $null
    try { $probe = $Hive.OpenSubKey($SubKey, $false) } catch { $probe = $null }
    if ($null -eq $probe) {
        Write-Info ('  [跳过] ' + $fullPath + '   (本来就不存在)')
        return
    }
    $probe.Dispose()

    try {
        $Hive.DeleteSubKeyTree($SubKey, $false)
    } catch [System.UnauthorizedAccessException] {
        Write-Info ('  [权限不足] ' + $fullPath)
        [void]$script:DeleteErrors.Add(($fullPath + ' -> ' + $_.Exception.Message))
        return
    } catch [System.Security.SecurityException] {
        Write-Info ('  [权限不足] ' + $fullPath)
        [void]$script:DeleteErrors.Add(($fullPath + ' -> ' + $_.Exception.Message))
        return
    } catch {
        [void]$script:DeleteErrors.Add(($fullPath + ' -> ' + $_.Exception.Message))
        return
    }

    # 回读确认真的删掉了
    $again = $null
    try { $again = $Hive.OpenSubKey($SubKey, $false) } catch { $again = $null }
    if ($null -ne $again) {
        $again.Dispose()
        [void]$script:DeleteErrors.Add(($fullPath + ' -> 删除后该项仍然存在'))
        return
    }
    Write-Info ('  [已删除] ' + $fullPath)
}

function Remove-RegValueAt {
    param(
        [Parameter(Mandatory = $true)]$Hive,
        [Parameter(Mandatory = $true)][string]$HiveLabel,
        [Parameter(Mandatory = $true)][string]$SubKey,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $fullPath = $HiveLabel + '\' + $SubKey + '  [' + $Name + ']'
    $key = $null
    try { $key = $Hive.OpenSubKey($SubKey, $true) } catch { $key = $null }
    if ($null -eq $key) {
        Write-Info ('  [跳过] ' + $fullPath + '   (本来就不存在)')
        return
    }
    try {
        if (@($key.GetValueNames()) -notcontains $Name) {
            Write-Info ('  [跳过] ' + $fullPath + '   (本来就不存在)')
            return
        }
        $key.DeleteValue($Name, $false)
    } catch [System.UnauthorizedAccessException] {
        Write-Info ('  [权限不足] ' + $fullPath)
        [void]$script:DeleteErrors.Add(($fullPath + ' -> ' + $_.Exception.Message))
        return
    } catch [System.Security.SecurityException] {
        Write-Info ('  [权限不足] ' + $fullPath)
        [void]$script:DeleteErrors.Add(($fullPath + ' -> ' + $_.Exception.Message))
        return
    } catch [System.IO.IOException] {
        [void]$script:DeleteErrors.Add(($fullPath + ' -> ' + $_.Exception.Message))
        return
    } finally {
        $key.Dispose()
    }

    # 回读确认真的删掉了
    $again = $null
    try { $again = $Hive.OpenSubKey($SubKey, $false) } catch { $again = $null }
    if ($null -ne $again) {
        try {
            if (@($again.GetValueNames()) -contains $Name) {
                [void]$script:DeleteErrors.Add(($fullPath + ' -> 删除后该值仍然存在'))
                return
            }
        } finally { $again.Dispose() }
    }
    Write-Info ('  [已删除] ' + $fullPath)
}

# ---------------------------------------------------------------------------
# input.dll!InstallLayoutOrTip —— 必须【在删注册表之前】调用
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
# 0. 前置检查
# ===========================================================================
Write-Host ''
Write-Host '形变输入法 (xingbian-ime) 反注册脚本' -ForegroundColor Green
Write-Info ('  安装目录    : ' + $InstallDir)
if ($PerUser) { Write-Info '  注册方式    : -PerUser（HKCU 实验路径）' } else { Write-Info '  注册方式    : 默认（HKLM）' }
Write-Info ('  语言列表串  : ' + $TipPsz)
Write-Info '  提示：HKCU 与 HKLM 两处注册都会尝试清理，幂等，不存在不算错误。'

if (-not [Environment]::Is64BitProcess) {
    Write-Err '当前是 32 位 PowerShell。本输入法是 x64 的 TSF 服务，必须用 64 位 PowerShell 反注册。'
    Write-Err '请改用 C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe，或直接运行 uninstall.cmd。'
    exit $ExitRegistry
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Warn '当前不是管理员：HKCU 下的注册可以清理，HKLM 下的项会因权限不足而跳过。'
    Write-Warn '请右键点击 uninstall.cmd，选择「以管理员身份运行」，以便把 HKLM 下的注册也清干净。'
}

# ===========================================================================
# 1. 先从语言列表移除（必须在删注册表之前）
# ===========================================================================
Write-Step '1/3 从语言列表移除（input.dll!InstallLayoutOrTip + ILOT_UNINSTALL）'
try {
    $ok = Invoke-InstallLayoutOrTip -Psz $TipPsz -Flags $ILOT_UNINSTALL
    if ($ok) {
        Write-Info ('  [OK] InstallLayoutOrTip(' + $TipPsz + ', ILOT_UNINSTALL=0x00000001) 成功')
    } else {
        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Write-Warn ('InstallLayoutOrTip 返回失败（Win32 错误码 ' + $err + '）。')
        Write-Warn '如果它本来就没被加进语言列表（比如注册完还没注销过），这是正常的；继续清理注册表。'
    }
} catch {
    Write-Warn ('调用 InstallLayoutOrTip 失败：' + $_.Exception.Message)
    Write-Warn '继续清理注册表。'
}

# ===========================================================================
# 2. 删除注册表
# ===========================================================================
Write-Step '2/3 删除注册表项'

# HKCU：不需要管理员，先做
Remove-RegTreeAt -Hive ([Microsoft.Win32.Registry]::CurrentUser) -HiveLabel 'HKCU' -SubKey ('Software\Microsoft\CTF\TIP\' + $Clsid)
Remove-RegTreeAt -Hive ([Microsoft.Win32.Registry]::CurrentUser) -HiveLabel 'HKCU' -SubKey ('Software\Classes\CLSID\' + $Clsid)

# 只删我们自己写的那一个值，不碰 HKCU\Software\XingbianIME 下别人的东西
Remove-RegValueAt -Hive ([Microsoft.Win32.Registry]::CurrentUser) -HiveLabel 'HKCU' -SubKey 'Software\XingbianIME' -Name 'DictPath'

# HKLM：需要管理员
$hklmDenied = $false
if (-not $isAdmin) {
    foreach ($sub in @(('SOFTWARE\Microsoft\CTF\TIP\' + $Clsid), ('SOFTWARE\Classes\CLSID\' + $Clsid))) {
        $probe = $null
        try { $probe = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($sub, $false) } catch { $probe = $null }
        if ($null -ne $probe) {
            $probe.Dispose()
            Write-Info ('  [权限不足] HKLM\' + $sub + '   (需要管理员，本次跳过)')
            $hklmDenied = $true
        } else {
            Write-Info ('  [跳过] HKLM\' + $sub + '   (本来就不存在)')
        }
    }
} else {
    Remove-RegTreeAt -Hive ([Microsoft.Win32.Registry]::LocalMachine) -HiveLabel 'HKLM' -SubKey ('SOFTWARE\Microsoft\CTF\TIP\' + $Clsid)
    Remove-RegTreeAt -Hive ([Microsoft.Win32.Registry]::LocalMachine) -HiveLabel 'HKLM' -SubKey ('SOFTWARE\Classes\CLSID\' + $Clsid)
}

if ($script:DeleteErrors.Count -gt 0) {
    Write-Host ''
    Write-Err ('注册表删除失败，共 ' + $script:DeleteErrors.Count + ' 项：')
    foreach ($e in $script:DeleteErrors) { Write-Err ('    ' + $e) }
    Write-Err '请确认以管理员身份运行 uninstall.cmd 后重试。'
    exit $ExitRegistry
}

# ===========================================================================
# 3. 可选：删除文件
# ===========================================================================
Write-Step '3/3 删除安装目录'
if (-not $RemoveFiles) {
    Write-Info ('  [保留] ' + $InstallDir)
    Write-Info '  默认不删文件以便排障；确实要删请加 -RemoveFiles。'
} else {
    if (-not (Test-Path -LiteralPath $InstallDir)) {
        Write-Info ('  [跳过] ' + $InstallDir + '   (本来就不存在)')
    } else {
        try {
            Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction Stop
            if (Test-Path -LiteralPath $InstallDir) { throw ('删除后目录仍然存在: ' + $InstallDir) }
            Write-Info ('  [已删除] ' + $InstallDir)
        } catch {
            if (Test-AccessDenied $_.Exception) {
                Write-Err ('删除安装目录失败：权限不足，请以管理员身份运行。（' + $_.Exception.Message + '）')
                exit $ExitNotAdmin
            }
            Write-Err ('删除安装目录失败：' + $_.Exception.Message)
            exit $ExitRegistry
        }
    }
}

# ===========================================================================
# 完成
# ===========================================================================
if ($hklmDenied) {
    Write-Host ''
    Write-Err 'HKLM 下仍有残留项未被清理（权限不足）。请右键以管理员身份运行 uninstall.cmd 再执行一次。'
    exit $ExitNotAdmin
}

if (-not $Quiet) {
    Write-Host ''
    Write-Host '========================================' -ForegroundColor Green
    Write-Host ' 形变输入法已反注册' -ForegroundColor Green
    Write-Host '========================================' -ForegroundColor Green
    Write-Host ''
    Write-Host '建议注销并重新登录（或重启），让 TSF 彻底卸掉它。'
    Write-Host '之后 Win + 空格 的输入法列表里就不应再有「形变输入法」。'
    Write-Host ''
    if (-not $RemoveFiles) {
        Write-Host ('安装目录仍然保留：' + $InstallDir)
        Write-Host '要连文件一起删掉，请执行：  .\unregister.ps1 -RemoveFiles'
        Write-Host ''
    }
}

exit $ExitSuccess
