#!/bin/bash
cd "$(dirname "$0")/../.."
export GAME=Stryktipset
export CHANNEL_MODE=LIVE
echo "Starting PlingBot - Stryktipset (LIVE)"
dotnet run --project src/PlingBot/PlingBot.csproj
