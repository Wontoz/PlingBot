#!/bin/bash
cd "$(dirname "$0")/../.."
export GAME=Topptipset
export CHANNEL_MODE=LIVE
echo "Starting PlingBot - Topptipset (LIVE)"
dotnet run --project src/PlingBot/PlingBot.csproj
