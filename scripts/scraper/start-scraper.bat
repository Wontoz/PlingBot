@echo off
cd /d "%~dp0..\.."

set PLAYER=%1
set GAME=%2

echo Running scraper...
echo Player: %PLAYER%
echo Game: %GAME%
echo.

dotnet run --project src\TipsScraper\TipsScraper.csproj -- --player %PLAYER% --game %GAME%

pause
