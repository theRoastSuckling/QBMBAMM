@echo off
setlocal

REM Builds and runs QBModsBrowser.Server from repo root.
set "ROOT=%~dp0"
pushd "%ROOT%"

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

echo.
echo Starting QBModsBrowser.Server...
dotnet run --project "src\QBModsBrowser.Server\QBModsBrowser.Server.csproj" --no-launch-profile -c Debug

set "EXIT_CODE=%ERRORLEVEL%"
popd

echo.
echo Server exited with code %EXIT_CODE%. Press any key to close...
pause >nul
exit /b %EXIT_CODE%
