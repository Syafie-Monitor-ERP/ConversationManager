@echo off
REM Runs both suites: parsing/search logic against synthetic transcripts, then offscreen layout.
setlocal
cd /d "%~dp0"

echo === Parsing, merge and search ===
dotnet run --project tests\ConversationManager.Tests\ConversationManager.Tests.csproj || exit /b 1

echo.
echo === Layout ===
dotnet run --project tests\ConversationManager.UiTests\ConversationManager.UiTests.csproj || exit /b 1

echo.
echo All suites passed.
endlocal
