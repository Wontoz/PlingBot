@echo off
cd /d %~dp0\..

echo Starting PlingBot...
echo.

dotnet run --project src\PlingBot\PlingBot.csproj

pause