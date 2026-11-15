@echo off
setlocal EnableExtensions
set "yak="
if defined P2_RHINO9_SYSTEM if exist "%P2_RHINO9_SYSTEM%\Yak.exe" set "yak=%P2_RHINO9_SYSTEM%\Yak.exe"
if not defined yak if exist "%ProgramFiles%\Rhino 9\System\Yak.exe" set "yak=%ProgramFiles%\Rhino 9\System\Yak.exe"
if not defined yak for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\McNeel\Rhinoceros\9.0\Install" /v Path 2^>nul') do (
    if /i "%%A"=="REG_SZ" if exist "%%B\Yak.exe" set "yak=%%B\Yak.exe"
)
if not defined yak (
    echo Rhino 9 package manager was not found.
    echo Set P2_RHINO9_SYSTEM to the Rhino 9 System directory and run this installer again.
    goto failed
)

set "package="
for %%F in ("%~dp0p2visiblelines-*-rh9*-win.yak") do if exist "%%~fF" (
    if defined package goto multiple
    set "package=%%~fF"
)
if not defined package (
    echo No Better Make2D package was found next to Install.cmd.
    goto failed
)
if not exist "%~dp0InstallToolbar.ps1" goto missing_toolbar
if not exist "%~dp0P2VisibleLines.rui" goto missing_toolbar
powershell.exe -NoProfile -NonInteractive -Command "if (Get-Process -Name Rhino -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero }) { exit 1 }"
if errorlevel 1 (
    echo Close Rhino 9, then run Install.cmd again.
    goto failed
)
"%yak%" install "%package%"
if errorlevel 1 goto failed
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0InstallToolbar.ps1"
if errorlevel 1 (
    echo The plug-in is installed, but the toolbar could not be updated.
    goto failed
)
echo.
echo Restart Rhino 9, then run BetterMake2D or BetterMake2DHidden.
if /i not "%~1"=="--no-pause" pause
exit /b 0

:missing_toolbar
echo Extract all the installation files before running Install.cmd.
goto failed

:multiple
echo Keep one Better Make2D package next to Install.cmd and run it again.
:failed
if /i not "%~1"=="--no-pause" pause
exit /b 1
