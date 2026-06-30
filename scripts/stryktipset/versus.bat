@echo off
cd /d "%~dp0..\.."
SET GAME=Stryktipset
SET CHANNEL_MODE=LIVE
SET MODE=VERSUS
echo Starting PlingBot - Stryktipset (VERSUS)
echo.
dotnet run --project src\PlingBot\PlingBot.csproj
pause
