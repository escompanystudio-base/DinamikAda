@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\setup-explorer-taskbar.ps1"
if errorlevel 1 (
    pause
    exit /b 1
)
call "%~dp0DinamikAda-Baslat.bat" --explorer-taskbar
