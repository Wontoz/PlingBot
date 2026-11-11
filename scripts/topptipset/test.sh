#!/bin/bash
cd "$(dirname "$0")/../.."
export GAME=Topptipset
export CHANNEL_MODE=TEST
echo "Starting PlingBot - Topptipset (TEST)"
dotnet run --project src/PlingBot/PlingBot.csproj
