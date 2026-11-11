#!/bin/bash
cd "$(dirname "$0")/.."
echo "Starting PlingBotWeb on http://localhost:5050..."
dotnet run --project src/PlingBotWeb/PlingBotWeb.csproj
