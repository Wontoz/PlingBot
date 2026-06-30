@echo off
cd /d "%~dp0..\.."
SET GAME=Europatipset
SET CHANNEL_MODE=LIVE
echo Starting PlingBot - Europatipset (LIVE)
echo.
dotnet run --project src\PlingBot\PlingBot.csproj
pause
