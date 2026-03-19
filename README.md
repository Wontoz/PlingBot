# PlingBot ![Version](https://img.shields.io/github/v/release/Wontoz/PlingBot)

Live football coupon tracking bot for Discord.



## Features

- Maps coupon matches to fixtures up to 3 days ahead
- Live polling of match scores
- Announces:
  - Goals
  - Cancelled goals
  - Red cards
- Recalculates total correct picks on score change
- Persists state to JSON between runs
- Optional local test mode for simulating events

## Requirements

- .NET 7 or newer
- A Discord bot token
- An [API-Football key](https://www.api-football.com/)

## Configuration

PlingBot uses environment variables for configuration.

Required variables:
- DISCORD_TOKEN
- DISCORD_CHANNEL_ID_TEST
- FOOTBALL_API_URL
- FOOTBALL_API_KEY

## Running the Bot

From the project root:

dotnet run --project src/PlingBot/PlingBot.csproj

Or use:
run.bat

## Storage

The bot stores runtime state (coupon data) in:

src/PlingBot/json/

This folder must exist. It is created automatically if missing.

Runtime JSON files (coupons) are not tracked by Git.

## License

This project is provided as-is for personal and educational use.

![alt text](https://github.com/Wontoz/PlingBot/blob/main/assets/icon1024.png "PlingBot's icon")
