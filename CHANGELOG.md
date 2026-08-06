## [3.1.5.2] - 2026-08-06
- Redesigned header to be full-width
- Refreshed color palette for light and dark theme
- Removed match-number badge from event list entries
- Cleaned up unused CSS
- Refactored bot backend into smaller services for improved maintainability

---

## [3.1.5.1] - 2026-07-28
- Expanded Head2Head section with further data
- Added logotypes to header
- Updated color schemes to better match topptipset logo

---

## [3.1.5] - 2026-07-27
- Added Head2Head section on both web app and discord (!h2h <n>)

---

## [3.1.4.4] - 2026-07-26
- Changed color of light theme stat pillar for clarity
- Remade how best/worst picks are calculated to only include finished fixtures

---
## [3.1.4.3] - 2026-07-25
- Bugfix in league name

---

## [3.1.4.2] - 2026-07-25
- Further style updates

---

## [3.1.4.1] - 2026-07-25
- Increased spacing of score-symbols
- Removed "Grundserien"-text in league info row

---
## [3.1.4] - 2026-07-25
- Updated color themes
- Updated design of web app

---
## [3.1.3.3] - 2026-07-19
- Updated header gradient
- Added league logos column

---
## [3.1.3.2] - 2026-07-18
- Fixed live minute alignment

---
## [3.1.3.1] - 2026-07-18
- Improved match row layout
- Standardized league logo sizing
- Fixed status alignment
- Improved live minute alignment

---
## [3.1.3] - 2026-07-18
- Redesigned match rows
- Added league logo column
- Improved team logo layout
- Simplified event icons
- Updated Discord event icons
- Improved live event styling
- Ignored Cloudflared config
- Fixed shell script permissions

---
## [3.1.2] - 2026-07-16
- Added Inter font
- Added light theme
- Added theme switcher
- Redesigned header
- Added match info section
- Improved responsive layout
- Updated spacing and typography

---
## [3.1.1] - 2026-07-05
- Improved VAR event updates
- Fixed missed goals after restart
- Fixed VAR score preservation
- Moved injuries from events
- Added suspension indicators
- Updated match detail layout
- Optimized API usage
- Added new teams to repository

---
## [3.1.0] - 2026-07-02
- Fixed extra time handling
- Improved event ordering
- Moved injuries to team tab
- Fixed yellow card duplicates
- Restored match statistics after restart
- Improved mobile tabs

---
## [3.0.0] - 2026-06-30 - The Web App Update
- Added match detail view
- Added live match statistics
- Added event history tab
- Added lineups and team information
- Added live round statistics panel
- Simplified live event feed
- Improved dashboard layout
- Improved mobile experience
- Improved coupon detection
- Improved restart synchronization
- Optimized Football API usage
- Improved payout retrieval
- Fixed VAR announcement issues
- Fixed second yellow card detection
- Fixed half-time state handling

---

## [2.5.2] - 2026-06-15

- Added persistent team registry mapping Svenska Spel display names to API names
- Created API id fetcher utility project
- Mapping now falls back to API name from when Home/Awaykey fails to match
- Added kickoff time to match objects.
- Matches in `NS`, `TBD` and `HT` status are now skipped during polling
- Skipped matches are only logged once per bot startup instead of on every poll tick
- Skip log format cleaned up
- Poll cycle separator is now suppressed when no matches have passed their scheduled kickoff
- Red card announcements now show the event's own elapsed time instead of the match's current elapsed time

---

## [2.5.1] - 2026-06-13

- Goal announcements now show one emoji per player in versus mode (e.g. `✅❌✅`)
- Event list now shows most recent event at the top instead of the bottom
- Trimming now removes the oldest events when the list is too long, keeping the newest visible
- Various bugfixes

---

## [2.5.0] - 2026-06-12

- Added `MODE=VERSUS` environment variable to run the bot in a multi-player comparison mode
- Score line at the bottom shows correct count per player
- Versus dashboard includes betting percentages at the end of each row (shared with normal mode)
- Various refactors shortening overall code length
- Symbol boxes widened to 2 chars (`| 1  X  2  |` style) for better visual spacing
- Versus player-column slot widths now scale dynamically to the longest player name
- Tip strings padded to 3 chars so `|` separators stay vertically aligned regardless of tip length

---

## [2.4.0] - 2026-06-11

### Commands
- Added `!events <n>` command to display all match events on demand
- Dashboard match minute display now uses consistent apostrophe alignment for all minute values

---

## [2.3.0] - 2026-06-11
- Added `!stats <n>` command to fetch and display live match statistics on demand
  - Shows possession, shots, corners, fouls, cards, saves and passes per team
  - Both the request message and the statistics block auto-delete after 1 minute
- Moved all announcement files to `Services/Announcements/` subfolder
- General refactors

---

## [2.2.0] - 2026-06-01
- Dashboard message now rotates its extra message on an interval during polling
- Dashboard displays current symbol, score, match time and betting percentages per row
- Major refactor of `AnnouncementService` — cleaner separation of announcement flow
- Major refactor of `ScorePollerService` — improved polling structure and readability

---

## [2.1.0] - 2026-04-16
- Integrated TipsScraper to PlingBot, now the tips scraping app and the discord bot is contained within the same repository.
- Disabled goal correction notifications as they were not working properly

## [2.0.2] - 2026-03-22

- Changed red card polling to only occur every second minute after bot launch
  - Polling every cycle caused a noticeable slowdown in match processing speed
- Extra time has also been added to match events.
---

## [2.0.1] - 2026-03-19

- Bot now handles and announces extra time again, which accidentally got removed in the previous version.
- Fixed JSON files being rewritten every poll even if nothing had changed.
  - Now it only updates when an announced event occurs (Goal, Cancellation or Red Card).
- Minor readability cleanup in announcement service method flow

---

## [2.0.0] - 2026-03-19

This version represents a structural cleanup and stabilization of the bot's live polling and evaluation logic.

---

### Core Architecture

- Moved solution file to repository root
- Moved testing functionality to separate class (TestService.cs)
  - No longer resides in `ScorePollerService`
- Improved dependency injection structure
- Cleaned up project structure under `src/PlingBot`
- Removed runtime JSON files (coupons) from version control

---

### Score Polling

- Extended fixture mapping to check matches up to 3 days ahead
  - Previously only same day matches where polled
- Introduced `IsFinished` flag to prevent reprocessing of completed matches
- Improved match mapping logs and diagnostics
- Refined polling interval handling

---

### Announcements

- Added cancelled goal (VAR) detection
- Added red card detection 
- Moved result symbol logic (✅ / ❌) to it's own method

---

### Coupon Evaluation

- Improved fallback logic when match object is not available

---

### Persistence (JSON Handling)

- Improved state synchronization when match ends
- Fixed path resolution issues when running from different working directories
- Cleaner JSON structure for match state

---

### Discord / Infrastructure

- Removed unused gateway intents
- Fixed intent warnings related to scheduled events and invites
- Improved channel resolution error handling
- Cleaned up test-mode startup logic

---

### Testing / Development

- Added a dedicated `TestService` for simulating:
  - Goals
  - Cancelled goals
  - Red cards
- Cleaned up batch file and startup configuration

---

## [1.3.1] - 2026-01-31

### Changed
- Minor readability improvements
- Code cleanup following JSON coupon refactor

---

## [1.3.0] - 2026-01-31

### Added
- JSON-based coupon structure replacing list-based handling
- Persistent coupon storage per round
- Dynamic metadata tracking (player, date, total correct)

### Changed
- Removed requirement to manually update classes each week
- Refactored coupon loading and saving logic

---

## [1.2.0] - 2026-01-21

### Added
- General bot feature improvements
- Internal updates to live handling and command processing

---

## [1.1.0] - 2025-10-23

### Added
- Initial README documentation
- Run helper script for easier local execution
- Environment configuration improvements

### Changed
- Project restructuring
- Cleanup of redundant package references
- Removal of unused files

---

## [1.0.0] - 2025-08-30

### Added
- Initial project setup
- Basic Discord bot structure
- Match polling foundation
- Basic coupon evaluation system
