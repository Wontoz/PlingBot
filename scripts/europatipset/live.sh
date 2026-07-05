#!/bin/bash
cd "$(dirname "$0")/../.."
export GAME=Europatipset
export CHANNEL_MODE=LIVE
echo "Starting PlingBot - Europatipset (LIVE)"
dotnet run --project src/PlingBot/PlingBot.csproj
