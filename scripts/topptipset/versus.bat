@echo off
cd /d "%~dp0..\.."
SET GAME=Topptipset
SET CHANNEL_MODE=LIVE
SET MODE=VERSUS
echo Starting PlingBot - Topptipset (VERSUS)
echo.
dotnet run --project src\PlingBot\PlingBot.csproj
pause
