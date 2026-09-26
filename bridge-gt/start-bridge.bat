@echo off
REM OpenLinker <- Subiekt GT bridge launcher.
REM Run this directly (double-click or `start-bridge.bat` from cmd). A console
REM window stays open showing live logs - closing it stops the bridge. Killing
REM any already-running bridge first avoids the "address already in use" crash
REM on port 5055/5056 (Kestrel does not retry a busy port).
setlocal
cd /d "%~dp0"

echo Stopping any bridge already running...
taskkill /F /IM GtBridge.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo Building...
dotnet build -c Debug
if errorlevel 1 (
    echo Build failed - see errors above.
    pause
    exit /b 1
)

echo Starting bridge on :5055 (https) / :5056 (http)...
dotnet run --no-build
