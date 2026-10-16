@echo off
setlocal EnableExtensions
chcp 936 >nul 2>nul
title 形变输入法 (xingbian-ime) - 安装

rem ============================================================================
rem  形变输入法 —— 安装包装器（自提权）
rem
rem  用法：
rem    install.cmd
rem    install.cmd -InstallDir "D:\XingbianIME"
rem    install.cmd -SkipCopy
rem    附加参数会原样转交给 register.ps1。
rem
rem  退出码（沿用 register.ps1 的约定）：
rem    0 = 成功
rem    2 = 权限不足 / UAC 被拒绝
rem    3 = 源文件缺失
rem    4 = 注册表写入或回读校验失败
rem ============================================================================

set "XBT_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%XBT_PS%" set "XBT_PS=powershell.exe"
set "XBT_SCRIPT=%~dp0register.ps1"
set "XBT_ARGS=%*"

echo.
echo  ============================================================
echo    形变输入法 (xingbian-ime) 安装程序
echo  ============================================================
echo.
echo    脚本         : %XBT_SCRIPT%
echo    默认安装目录 : C:\Program Files\XingbianIME
if defined XBT_ARGS echo    附加参数     : %XBT_ARGS%
echo.
echo    本程序需要管理员权限，接下来会弹出 UAC 提示，请点「是」。
echo.

if not exist "%XBT_SCRIPT%" (
    echo  [错误] 找不到 register.ps1：
    echo         %XBT_SCRIPT%
    echo         请确认 register.ps1 与本文件在同一个目录里。
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
if "%XBT_RC%"=="3" goto :nosource
goto :failed

:ok
echo  ============================================================
echo    [成功] 形变输入法已经注册进系统
echo  ============================================================
echo.
echo    接下来请手动做这几步：
echo.
echo      1) 让 TSF 重新加载（二选一）
echo         . 最稳妥：注销并重新登录（或重启电脑）
echo         . 快一点：重启 ctfmon.exe，在 PowerShell 里执行
echo              Stop-Process -Name ctfmon -Force -ErrorAction SilentlyContinue
echo.
echo      2) 按 Win + 空格 切换输入法，列表里应该能看到「形变输入法」
echo.
echo      3) 打开记事本敲 nima，应弹出候选窗；按空格上屏得到：亻尔 女马
echo.
echo    中英文切换：单击 Shift
echo    卸载      ：右键以管理员身份运行 uninstall.cmd
echo.
pause
exit /b 0

:notadmin
echo  ============================================================
echo    [失败] 权限不足，安装没有执行
echo  ============================================================
echo.
echo    请在 UAC 提示里点「是」。
echo.
echo    如果你不想用管理员权限，可以试试未经验证的实验路线：
echo        powershell -ExecutionPolicy Bypass -File "%XBT_SCRIPT%" -PerUser
echo    注意：HKCU 这条路线本机未经验证，Win+空格 里可能看不到它。
echo.
pause
exit /b 2

:nosource
echo  ============================================================
echo    [失败] 源文件缺失，安装没有执行
echo  ============================================================
echo.
echo    需要先编译出 Release 产物：
echo        src\Xingbian.Tsf\bin\Release\Xingbian.Tsf.dll
echo        src\Xingbian.Core\bin\Release\Xingbian.Core.dll
echo    Xingbian.Core.dll 必须和 Xingbian.Tsf.dll 一起复制到同一个目录，
echo    否则 CLR 加载 TextService 会失败。
echo.
echo    也可以用 -Source / -Dict 指定别的路径，例如：
echo        install.cmd -Source "D:\build" -Dict "D:\build\xingbian.bin"
echo.
pause
exit /b 3

:failed
echo  ============================================================
echo    [失败] 退出码 %XBT_RC%
echo  ============================================================
echo.
echo    4 = 注册表写入或回读校验失败
echo        上面管理员窗口里的错误信息会指出是哪一项；也可以照着
echo        install\README.md 的「手动安装」一节逐条手工注册。
echo.
echo    其它退出码请对照管理员窗口里的错误信息。
echo.
pause
exit /b %XBT_RC%
