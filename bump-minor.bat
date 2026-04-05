@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "bump or publish helpers\bump-version.ps1" -BumpType minor -ProjectPath "src\QBModsBrowser.Server\QBModsBrowser.Server.csproj"
pause
