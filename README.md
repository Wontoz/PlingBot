# PlingBot

PlingBot is a Discord bot that tracks live football matches from a Stryktips/Europatips/Topptips coupon and posts match updates directly to a Discord channel.

The bot announces:

- Goals (with correct player attribution)
- Goal cancellations (VAR decisions)
- Red cards
- Current coupon status via command

---

## Features

- Event-based goal tracking using official match events
- Support for goal cancellations (VAR)
- Duplicate-safe announcements
- Live coupon evaluation
- JSON-based coupon storage
- Discord command support (`!status`)
- Optional startup test mode

---

## Commands

### `!status`
Displays current number of correct picks.

---

## Configuration

The bot requires environment variables for:

- Discord bot token
- Discord channel IDs (Where the bot will post messages)
- Football API URL + Key
- (Optional) Allowed user IDs

---

## Running the Bot

Coming soon!
