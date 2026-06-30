@echo off
cd /d "%~dp0..\.."
SET GAME=Topptipset
SET CHANNEL_MODE=TEST
echo Starting PlingBot - Topptipset (TEST)
echo.
dotnet run --project src\PlingBot\PlingBot.csproj
pause
