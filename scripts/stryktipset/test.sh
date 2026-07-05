#!/bin/bash
cd "$(dirname "$0")/../.."
export GAME=Stryktipset
export CHANNEL_MODE=TEST
echo "Starting PlingBot - Stryktipset (TEST)"
dotnet run --project src/PlingBot/PlingBot.csproj
