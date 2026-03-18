# Changelog

All notable changes to this project will be documented in this file.

This project follows Semantic Versioning.

---

## [2.0.0] - Coming Soon!

### Added
- Event-based live engine for goal tracking
- Full support for goal cancellations (VAR decisions)
- Tracking of announced goal/event history to prevent duplicates
- Improved coupon state handling in JSON
- Safer live state persistence
- Evaluation fallback using stored scores

### Changed
- Goal detection logic now relies on official match events instead of score differences
- Cancellation handling is processed before new goal announcements
- Score formatting now highlights updated values for both goals and cancellations
- Live updates are more resilient to delayed API responses
- Coupon evaluation no longer depends on runtime-polled match objects
- Extra time explicitly ignored in accordance with Stryktipset / Europatips rules

### Fixed
- Duplicate goal announcements
- Incorrect scorer attribution
- Re-announcing cancelled goals
- Evaluation failing when matches had not yet been polled

---

## [1.0.1] - 2026-01-31

### Changed
- Minor readability improvements
- Code cleanup following JSON coupon refactor

---

## [1.0.0] - 2026-01-31

### Added
- JSON-based coupon structure replacing list-based handling
- Persistent coupon storage per round
- Dynamic metadata tracking (player, date, total correct)

### Changed
- Removed requirement to manually update classes each week
- Refactored coupon loading and saving logic

---

## [0.4.0] - 2026-01-21

### Added
- General bot feature improvements
- Internal updates to live handling and command processing

---

## [0.3.0] - 2025-10-23

### Added
- Initial README documentation
- Run helper script for easier local execution
- Environment configuration improvements

### Changed
- Project restructuring
- Cleanup of redundant package references
- Removal of unused files

---

## [0.1.0] - 2025-10-23

### Added
- Initial project setup
- Basic Discord bot structure
- Match polling foundation
- Basic coupon evaluation system
