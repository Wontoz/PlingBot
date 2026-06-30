@echo off
cd /d "%~dp0..\.."
SET GAME=Stryktipset
SET CHANNEL_MODE=TEST
echo Starting PlingBot - Stryktipset (TEST)
echo.
dotnet run --project src\PlingBot\PlingBot.csproj
pause
