@echo off
setlocal

REM Builds and runs QBModsBrowser.Server from repo root.
set "ROOT=%~dp0"
pushd "%ROOT%"

REM Kill any running server instance before building so the output exe is not locked.
REM The exe is named QBMBAMM.exe in Debug/Release builds.
echo Checking for running QBMBAMM / QBModsBrowser.Server process...
set "KILLED=0"
tasklist /FI "IMAGENAME eq QBMBAMM.exe" 2>nul | find /I "QBMBAMM.exe" >nul
if not errorlevel 1 (
    echo Found QBMBAMM.exe. Killing it...
    taskkill /F /IM "QBMBAMM.exe" >nul 2>&1
    set "KILLED=1"
)
tasklist /FI "IMAGENAME eq QBModsBrowser.Server.exe" 2>nul | find /I "QBModsBrowser.Server.exe" >nul
if not errorlevel 1 (
    echo Found QBModsBrowser.Server.exe. Killing it...
    taskkill /F /IM "QBModsBrowser.Server.exe" >nul 2>&1
    set "KILLED=1"
)
if "%KILLED%"=="1" (
    timeout /t 2 /nobreak >nul
    echo Done.
)

echo Building QBModsBrowser.Server (Debug)...
dotnet build "src\QBModsBrowser.Server\QBModsBrowser.Server.csproj" -c Debug

if errorlevel 1 (
    echo.
    echo Build failed. Server was not started.
    echo Press any key to close...
    pause >nul
    popd
    exit /b 1
)

set "EXE=%ROOT%src\QBModsBrowser.Server\bin\Debug\net10.0-windows\QBMBAMM.exe"

echo.
echo Starting QBMBAMM.exe...
start "" "%EXE%"

echo.
echo Server launched. Closing in 3 seconds...
timeout /t 3 /nobreak >nul

popd
exit /b 0
