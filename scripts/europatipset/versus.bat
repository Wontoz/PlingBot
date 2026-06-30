@echo off
cd /d "%~dp0..\.."
SET GAME=Europatipset
SET CHANNEL_MODE=LIVE
SET MODE=VERSUS
echo Starting PlingBot - Europatipset (VERSUS)
echo.
dotnet run --project src\PlingBot\PlingBot.csproj
pause
