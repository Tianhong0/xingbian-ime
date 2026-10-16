@echo off
setlocal EnableExtensions
chcp 936 >nul 2>nul
title 形变输入法 (xingbian-ime) - 卸载

rem ============================================================================
rem  形变输入法 —— 卸载包装器（自提权）
rem
rem  用法：
rem    uninstall.cmd
rem    uninstall.cmd -RemoveFiles
rem    uninstall.cmd -InstallDir "D:\XingbianIME" -RemoveFiles
rem    附加参数会原样转交给 unregister.ps1。
rem
rem  退出码（沿用 unregister.ps1 的约定）：
rem    0 = 成功
rem    2 = 权限不足 / UAC 被拒绝 / HKLM 下仍有残留
rem    4 = 注册表删除失败
rem ============================================================================

set "XBT_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%XBT_PS%" set "XBT_PS=powershell.exe"
set "XBT_SCRIPT=%~dp0unregister.ps1"
set "XBT_ARGS=%*"

echo.
echo  ============================================================
echo    形变输入法 (xingbian-ime) 卸载程序
echo  ============================================================
echo.
echo    脚本         : %XBT_SCRIPT%
echo    默认安装目录 : C:\Program Files\XingbianIME
if defined XBT_ARGS echo    附加参数     : %XBT_ARGS%
echo.
echo    本程序需要管理员权限，接下来会弹出 UAC 提示，请点「是」。
echo.
echo    注意：默认只删注册表，【保留】安装目录里的文件以便排障。
echo          要连文件一起删掉，请加参数：uninstall.cmd -RemoveFiles
echo.

if not exist "%XBT_SCRIPT%" (
    echo  [错误] 找不到 unregister.ps1：
    echo         %XBT_SCRIPT%
    echo         请确认 unregister.ps1 与本文件在同一个目录里。
    echo.
    pause
    exit /b 3
)

rem 把参数数组交给 Start-Process；脚本路径用 [char]34 显式加引号，这样安装目录
rem 里带空格（例如 D:\My IME）也能正确传递。
rem 注意：这里【不能】给提权进程加 -NoExit —— 加了它提权窗口永不退出，
rem Start-Process -Wait 会一直挂住，而且返回码也会丢掉。
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$q=[char]34; $a=@('-NoProfile','-ExecutionPolicy','Bypass','-File',($q+$env:XBT_SCRIPT+$q)); if($env:XBT_ARGS){$a+=$env:XBT_ARGS}; try { $p=Start-Process -FilePath $env:XBT_PS -Verb RunAs -PassThru -Wait -ArgumentList $a -ErrorAction Stop } catch { Write-Host ''; Write-Host ('[错误] 没能获得管理员权限：' + $_.Exception.Message) -ForegroundColor Red; Write-Host '如果你在 UAC 提示里点了「否」，请重新运行本程序并点「是」。' -ForegroundColor Yellow; exit 2 }; exit $p.ExitCode"
set "XBT_RC=%ERRORLEVEL%"

echo.
if "%XBT_RC%"=="0" goto :ok
if "%XBT_RC%"=="2" goto :notadmin
goto :failed

:ok
echo  ============================================================
echo    [成功] 形变输入法已反注册
echo  ============================================================
echo.
echo    建议注销并重新登录（或重启），让 TSF 彻底卸掉它。
echo    之后 Win + 空格 的输入法列表里就不应再有「形变输入法」。
echo.
echo    安装目录默认仍然保留，以便排障。要连文件一起删掉，请执行：
echo        uninstall.cmd -RemoveFiles
echo.
pause
exit /b 0

:notadmin
echo  ============================================================
echo    [失败] 权限不足，卸载不完整
echo  ============================================================
echo.
echo    请在 UAC 提示里点「是」。
echo    HKLM 下的注册表项必须由管理员删除，否则输入法还在。
echo.
pause
exit /b 2

:failed
echo  ============================================================
echo    [失败] 退出码 %XBT_RC%
echo  ============================================================
echo.
echo    上面管理员窗口里的错误信息会指出是哪一项没删掉。
echo    请确认以管理员身份运行后重试。
echo.
pause
exit /b %XBT_RC%
