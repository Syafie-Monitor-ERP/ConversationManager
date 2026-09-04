@echo off
REM Launches the published app, publishing it first if it is not there yet.
setlocal
cd /d "%~dp0"

if not exist "dist\ConversationManager.exe" (
    echo First run - building dist\ConversationManager.exe ...
    call publish.cmd || exit /b 1
)

start "" "dist\ConversationManager.exe"
endlocal
