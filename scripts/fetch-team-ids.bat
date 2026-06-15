@echo off
cd /d %~dp0\..

echo Fetching missing team IDs...
echo.

dotnet run --project src\TeamIdFetcher\TeamIdFetcher.csproj

pause
