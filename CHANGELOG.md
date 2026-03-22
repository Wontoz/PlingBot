# Changelog
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
