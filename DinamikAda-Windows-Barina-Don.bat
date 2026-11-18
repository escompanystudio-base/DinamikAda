@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\restore-explorer-taskbar.ps1"
if errorlevel 1 pause
