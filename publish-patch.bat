@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "build\bump-and-publish.ps1" -BumpType patch %*
pause
