@echo off
cd /d "%~dp0..\.."

set GAME=Europatipset
set DATE=%1

if "%DATE%"=="" set DATE=%date:~0,4%-%date:~5,2%-%date:~8,2%

echo Scraping payouts for %GAME% on %DATE%...
dotnet run --project src\TipsScraper\TipsScraper.csproj -- --game %GAME% --payouts-only --date %DATE%

pause
