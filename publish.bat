@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "bump or publish helpers\publish.ps1" %*
pause
