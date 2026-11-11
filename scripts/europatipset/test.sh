#!/bin/bash
cd "$(dirname "$0")/../.."
export GAME=Europatipset
export CHANNEL_MODE=TEST
echo "Starting PlingBot - Europatipset (TEST)"
dotnet run --project src/PlingBot/PlingBot.csproj
