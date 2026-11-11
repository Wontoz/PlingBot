#!/bin/bash
cd "$(dirname "$0")/../.."
PLAYER=$1
GAME=$2
echo "Running scraper — Player: $PLAYER, Game: $GAME"
dotnet run --project src/TipsScraper/TipsScraper.csproj -- --player "$PLAYER" --game "$GAME"
