@echo off
cd /d "%~dp0..\.."
SET GAME=Topptipset
SET CHANNEL_MODE=LIVE
echo Starting PlingBot - Topptipset (LIVE)
echo.
dotnet run --project src\PlingBot\PlingBot.csproj
pause
