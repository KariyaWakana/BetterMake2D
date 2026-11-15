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
    echo No P2VisibleLines package was found next to Install.cmd.
    goto failed
)
"%yak%" install "%package%"
if errorlevel 1 goto failed
echo.
echo Restart Rhino 9, then run P2VisibleLines or P2HiddenLines.
if /i not "%~1"=="--no-pause" pause
exit /b 0

:multiple
echo Keep one P2VisibleLines package next to Install.cmd and run it again.
:failed
if /i not "%~1"=="--no-pause" pause
exit /b 1
