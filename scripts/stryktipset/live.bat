@echo off
cd /d "%~dp0..\.."
SET GAME=Stryktipset
SET CHANNEL_MODE=LIVE
echo Starting PlingBot - Stryktipset (LIVE)
echo.
dotnet run --project src\PlingBot\PlingBot.csproj
pause
