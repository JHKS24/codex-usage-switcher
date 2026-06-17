@echo off
rem End-user installer launcher: runs install.ps1 from this same folder.
rem Usage: install.cmd [-NoStartup] [-NoLaunch]
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
if errorlevel 1 (
  echo.
  echo Install failed. See the messages above.
)
echo.
pause
