@echo off
cd /d "%~dp0.."
echo Starting Cloudflare tunnel...
start "PlingBot Tunnel" "%~dp0..\cloudflared\start-tunnel.bat"
echo Starting PlingBotWeb on http://localhost:5050...
echo.
dotnet run --project src\PlingBotWeb\PlingBotWeb.csproj
pause
