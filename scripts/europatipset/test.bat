@echo off
cd /d "%~dp0..\.."
SET GAME=Europatipset
SET CHANNEL_MODE=TEST
echo Starting PlingBot - Europatipset (TEST)
echo.
dotnet run --project src\PlingBot\PlingBot.csproj
pause
