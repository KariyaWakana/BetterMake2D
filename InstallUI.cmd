@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Component UI %*
set "INSTALL_EXIT=%ERRORLEVEL%"
pause
exit /b %INSTALL_EXIT%
