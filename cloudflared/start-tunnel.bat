@echo off
"D:\Program\cloudflared\cloudflared-windows-amd64.exe" tunnel --config "%~dp0config.yml" run
