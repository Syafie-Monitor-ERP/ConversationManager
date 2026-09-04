@echo off
REM Builds ConversationManager into a single self-contained exe under .\dist
REM Requires the .NET SDK once, on this machine only. The produced exe needs nothing installed.
setlocal
cd /d "%~dp0"

echo Publishing ConversationManager...
dotnet publish src\ConversationManager\ConversationManager.csproj -c Release -o dist
if errorlevel 1 (
    echo.
    echo Publish FAILED.
    exit /b 1
)

echo.
echo Done. Run:  dist\ConversationManager.exe
endlocal
