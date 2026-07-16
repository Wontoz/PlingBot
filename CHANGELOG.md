# Changelog

## [3.1.2] - 2026-07-16

### Webbappförbättringar

#### Nytt teckensnitt
- Inter ersätter systemfonten i hela webbappen — renare och mer konsekvent typografi

#### Ljust/mörkt läge
- Temaknappar i headern låter användaren växla mellan mörkt och ljust läge
- Valt tema sparas i `localStorage` och återställs vid nästa besök
- Ljust läge har ett genomtänkt eget färgtema (bakgrund, text, accentfärger, badges)

#### Headerdesign
- Headern får färg baserat på speltyp: Stryktipset (mörkblå), Europatipset (grön), Topptipset (orange), Annat (röd)
- Händelselistan och statistikpanelen sitter nu tätt mot headern utan mellanrum — ökar tillgänglig höjd för innehållet
- Sektionerna har raka toppkanter (rundning bara i underkanten), passande att de gränsar direkt mot headern

##### Ligarad och omgångsvisning
- Ligaraden under lagnamnen döljs nu redan vid 1400px bredd (var 892px) — undviker trång visning vid mellanstora fönster
- Omgångssträngar översätts nu till svenska i backend och sparas som ett eget fält (`RoundSwedish`): "Regular Season" → "Grundserien", "- 11" → "- Omgång 11". Webben använder `RoundSwedish` och faller tillbaka på rå API-text för äldre JSON-filer

#### Matchinfo i statistikfliken
- Längst ner i Statistik-fliken visas nu en "Matchinfo"-sektion med liga/omgång och arena — synlig även om matchstatistik ännu inte finns tillgänglig

### Layoutförbättringar
- Minskad padding i header, main-layout och footerrad för att kompensera att Inter upptar något mer utrymme
- Händelseraderna i Live- och matchflödena har fått ökad höjd — luftigare och lättare att läsa

---

## [3.1.1] - 2026-07-05

### VAR-händelser uppdateras nu i efterhand
- När API:et initialt inte känner till spelarnamn vid en bortdömning visas händelsen ändå direkt — och fylls sedan automatiskt på med spelarnamn och orsak (t.ex. "Offside") när informationen finns tillgänglig
- Discord-meddelandet uppdateras på samma sätt som målmeddelanden redan gör
- Orsaken till bortdömningen visas nu i både Discord och webbappen, med stor inledningsbokstav
- När ett bortdömt mål inträffar tas det ursprungliga målkortet bort ur händelseflödet

### Buggfix: mål missades vid botomstart mitt i en match
- Om botten startades om medan en match pågick, antog den att alla mål som redan inträffat var kända — och om något mål senare kom in i cachen behandlades det som dubblett och annonserades aldrig. Nu bevaras det sparade ställningsvärdet oförändrat vid omstart för pågående matcher, så upptagningsfasen fungerar korrekt

### Buggfix: VAR-händelsers ställning uppdaterades felaktigt
- När spelarinfo fylldes i på en VAR-händelse ersattes av misstag originalställningen (vid bortdömningstillfället) med nuvarande matchställning. Nu används alltid den ursprungligt sparade texten som bas

### Frånvarolistan visas inte längre i Händelser-fliken
- Skade- och avstängningsrapporter lagras nu som ett eget fält per match i JSON-filen, separerade från matchhändelserna — de kan inte längre råka trigga Händelser-fliken att visas i onödan
- Dubbletter i skadelistan (samma spelare dök upp flera gånger vid batchhämtning) är åtgärdade

### Nya ikoner för skador och avstängningar
- Skadeikonen är nu ett rött kors istället för ett plåster
- Spelare som är **avstängda** (gult kort i tidigare match) får en ny ikon med ett gult kort och en förbudscirkel framför, och texten "Avstängd" — de hanterades tidigare likadant som skadade spelare trots att orsaken är en annan

### Webbappförbättringar
- Ligalogotypen visas nu direkt till vänster om flaggan istället för längst till vänster i ligaraden
- Fliken "Laguppställning" heter nu "Laginfo" (matchar bättre att den också innehåller frånvaroinformation)

### Effektivare API-användning
- Händelse- och statistikhämtning vid synkronisering (varje poll) och ikappkörning (botomstart) sker nu i ett enda batchanrop per omgång istället för ett anrop per match — minskar API-belastningen avsevärt
- Flera interna API-metoder som inte längre används har tagits bort

### Nya lag i lagrepositoriet
Akranes, Breidablik, Bryne, Hödd, Ilves, Kalmar, Sandnes, Strömsgodset, Valur, Vestmannaeyja, Örgryte

---

## [3.1.0] - 2026-07-02

### Extra Time-buggfixar
- Botten annonserar inte längre mål eller händelser som inträffade under förlängning eller straffläggning — BT (Before Penalties) och P (Penalties) behandlas nu som inaktiva perioder, precis som ET
- Matcher som avgjordes via förlängning (AET) eller straffar (PEN) visar nu rätt ställning från 90 minuter — tidigare sparades slutresultatet efter förlängningen
- Händelser-fliken visar inte längre händelser med elapsed > 90 minuter
- Backfill (ikappkörning vid botomstart) filtrerar nu också bort förlängningstid-händelser

### Händelser-fliken
- Händelser sorteras nu på elapsed-tid (senaste överst) — tidigare kunde backfillade matcher visa händelser i fel ordning eftersom alla backfillade poster fick samma tidsstämpel
- Målhändelser visar nu det svenska lagnamnet från kupongen istället för engelska API-namnet ("Spain" → "Spanien")

### Frånvaro-sektionen flyttad
- Skaderapporter visas nu i Laguppställning-fliken under avbytarsektionen, inte i Händelser
- Laguppställning-fliken öppnas nu även när det ännu inte finns någon laguppställning, om skaderapporter finns tillgängliga

### Gult-kort-dubblettfix
- När API:et rapporterar ett gult kort utan spelarnamn ("Okänd") och sedan fyller i namnet i en senare poll slås de nu korrekt ihop till en enda händelse — tidigare kunde elapsed-minuten ha förskjutits en hel minut mellan svaren, vilket skapade ett dubblettevent

### Matchstatistik vid omstart
- Passnings- och matchstatistik (bollinnehav, passningar, skott m.m.) hämtas nu och sparas vid botstart även för redan avslutade matcher — tidigare saknades statistiken efter en omstart

### Mobilanpassade flikar
- Fliketiketterna förkortas på skärmar ≤820px (Stats, Händelser, Lag) så att alla flikar ryms utan horisontalscroll

---

## [3.0.0] - 2026-06-30 - The Web App Update

### New match detail view in the web dashboard
- Click any match row to open a match-specific view with up to three tabs: **Statistik**, **Händelser** and **Laguppställning**
- A tab only appears if there's actually data to show for that match
- **Statistik**: possession, shots (total/on target/off target/blocked), corners, fouls, pass accuracy, cards and saves — each row has a visual comparison bar between the two teams, with the leading team shown in red and the other in white
- **Händelser**: every goal, card, substitution and VAR event for the match, grouped under "First half" / "Second half"
- **Laguppställning**: starting lineup and substitutes for both teams side by side (with shirt number and position), formations (e.g. 4-3-3), and coaches including photo
- Clicking a row only switches the detail view if you're already looking at another match's tab — otherwise you stay on the Live feed and just see the tabs become available
- The selected match row gets a light blue background so it's clear which match you're looking at

### Cleaner event feed
- Goals, cards, penalties, VAR overturns and substitutions now have custom-drawn icons instead of emoji
- The "Live" feed now only shows goals, red cards and overturned goals — substitutions and regular yellow cards instead show up in the new match detail view's Händelser tab, keeping the main feed less noisy
- The VAR icon is now clearer to read (it used to be shrunk inside an oversized invisible box)

### New stats panel during a live round
- "Best/worst value" (only relevant before the coupon is submitted) is now replaced once the round is live or finished with three more relevant views:
  - **Våra bästa drag** ("our best moves") — matches where we're currently right with a pick most others didn't make
  - **Fällor** ("traps") — matches where we (and most others) trusted the favorite and got it wrong
  - **Största överraskningarna** ("biggest surprises") — the most unexpected results in the round
- The value-bet view is unchanged in the pre-round preview

### Layout and appearance fixes
- Shorter rounds (e.g. Topptipset's 8 matches) now use the same compact, polished layout as longer ones (Stryktipset) — they previously looked off with odds/percentages in the wrong order
- Various spacing, alignment and color polish across the statistics and lineup views
- On mobile, only the Live feed scrolls in its own box — Statistik/Händelser/Laguppställning instead take up the full page height so nothing gets cut off

### Bot finds the right coupon automatically
- On startup, the bot now searches backwards through dates for the most recent coupon that actually exists, instead of blindly creating an empty one for today if nothing is found
- Catching up on missed data (after the bot was offline) now also fetches match statistics and lineups, not just goals and cards
- Fixed a bug where matches that had already been caught up on once would never get a chance to fetch newer kinds of data after a bot update

### More reliable football API usage
- The bot now learns the account's actual rate limit automatically instead of guessing, and stays safely under it
- If the daily quota is running low, the bot automatically slows down its requests instead of risking getting blocked
- Fewer unnecessary requests overall thanks to smarter caching of data that's already been fetched

### Payouts (kr per correct pick) are fetched more reliably
- The bot now also tries to fetch the payout when a match finishes, not just when a goal is scored — payouts are often posted a while after the last match's final whistle, not right after the last goal
- Longer, smarter retry window, plus an extra check on startup in case the bot is started after the whole round has already finished

### Other bug fixes
- Certain VAR-overturned goal texts (e.g. "Goal Disallowed - Foul") weren't being announced — fixed
- Second-yellow red cards weren't always caught correctly during a live match — fixed
- Half-time (HT) status sometimes wasn't saved correctly if the match had already passed half-time when the bot started
- Removed unused test code that was no longer needed

---

## [2.5.2] - 2026-06-15

### Team Registry (`teams.json`)
- Added persistent team registry (`src/PlingBot/data/teams.json`) mapping Swedish display names to API names and team IDs
- Populated with ~250 entries sourced from historical coupon data
- `TeamRepository` loads the registry on startup and upserts entries whenever a fixture is mapped, keeping IDs up to date automatically
- New `TeamIdFetcher` utility project (`scripts/fetch-team-ids.bat`) — searches the football API for missing team IDs, auto-fills exact matches and prompts interactively for ambiguous ones

### Fixture Mapping
- Mapping now falls back to `ApiName` from `TeamRepository` when `HomeKey`/`AwayKey` fails to match — fixes cases where TipsScraper stores the Swedish name (e.g. `"Saudiarabien"`) instead of the API name (`"Saudi Arabia"`)
- `KickoffUtc` field added to `TipsMatch` — saved to JSON when a fixture is mapped so the kickoff time persists across restarts
- `HasMatchesInPlay()` now uses the persisted `KickoffUtc` instead of the in-memory `Match.Date`

### Polling
- Matches in `NS`, `TBD` and `HT` status are now skipped during polling (no score changes possible), in addition to the existing `ET` skip
- Skipped matches are only logged once per bot startup instead of on every poll tick
- Skip log format cleaned up — team names no longer wrapped in parentheses, kickoff shown as `MM-dd HH:mm` (`Match #2  Saudiarabien - Uruguay  Not Started  06-15 22:00`)
- Poll cycle separator is now suppressed when no matches have passed their scheduled kickoff, keeping the console silent until matches go live

### Announcements
- Red card announcements now show the event's own elapsed time instead of the match's current elapsed time, giving a more accurate minute

### Commands
- `!events` label column is now padded using emoji-aware display width so the team-name column stays aligned when labels contain emoji characters (e.g. `⚽MÅL` vs `🟨GULT KORT`)

---

## [2.5.1] - 2026-06-13

### Versus Mode
- Goal announcements now show one emoji per player in versus mode (e.g. `✅❌✅`) instead of a single shared emoji
- `!status` in versus mode now shows eachs coupons score
- Added fallback command `!procent <matchnr> <1%> <X%> <2%>` command to update betting percentages in-memory and persist to JSON without restarting the bot

### Announcements
- Own goals now always include `(Självmål)` in the event text, even when no scorer is known
- Penalty suffix `(Straff)` similarly preserved when no scorer is known (same code path)

### Dashboard
- Event list now shows most recent event at the top instead of the bottom (both normal and versus dashboard)
- Fixed: trimming now removes the oldest events when the list is too long, keeping the newest visible

### Match Display
- Fixed minute alignment: times without extra time (e.g. `45'`) now align with extra-time formats (e.g. `45+1'`) — both start one position from the left edge of the status column

### Bug Fixes
- `HasMatchesInPlay()` now converts match date to UTC before comparing with `DateTime.UtcNow`, preventing a timezone mismatch that could suppress the live data overlay

---

## [2.5.0] - 2026-06-12

### Versus Mode
- Added `MODE=VERSUS` environment variable to run the bot in a multi-player comparison mode
- New `VersusConfig` — loads `{game}_{date}_versus.json` containing other players' tips alongside the primary player's picks
- New `VersusDashboardBuilder` — renders all players' tips side-by-side per match row with emoji result indicators
- `DashboardService` branches on `BotOptions.IsVersusMode` to select the correct builder at runtime
- Score line at the bottom shows correct count per player
- Versus dashboard includes betting percentages at the end of each row (shared with normal mode)

### Refactoring
- Extracted `MatchDisplayFormatter` static helper class — shared formatting logic previously duplicated across builders
  - `FormatSymbolBox`, `FormatMatchText`, `FormatStatusAndScore`, `GetMatchColumnWidth`, `GetFixtureStatus`, `GetScore`, `FormatPercentages`
- `DashboardBuilder` reduced from ~260 to ~95 lines after extraction
- `FormatPercentages` moved from `DashboardBuilder` to `MatchDisplayFormatter` so both builders share one implementation

### Dashboard Alignment
- Symbol boxes widened to 2 chars (`| 1  X  2  |` style) for better visual spacing
- Versus player-column slot widths now scale dynamically to the longest player name
- `prefixWidth` for the versus header row computed from actual `FormatSymbolBox` output length, preventing misalignment when symbol box width changes
- Tip strings padded to 3 chars so `|` separators stay vertically aligned regardless of tip length (`1`, `X2`, `1X2`)

---

## [2.4.0] - 2026-06-11

### Commands
- Added `!events <n>` command to display all match events on demand
  - Events grouped by period: HALVLEK 1, HALVLEK 2, FÖRLÄNGNING 1/2
  - Running score updated after each goal, including own goals
  - Substitution order shown in label (BYTE 1, BYTE 2, …)
  - Current match score shown in the header
  - Team resolved from API data; home/away determined via TeamId to avoid name mapping issues

### Fixes
- Dashboard match minute display now uses consistent apostrophe alignment for all minute values (1–90) via dynamic padding
- `TipsScraper` aliasMap dictionary sorted alphabetically per league section

---

## [2.3.0] - 2026-06-11

### Commands
- Added `!stats <n>` command to fetch and display live match statistics on demand
  - Shows possession, shots, corners, fouls, cards, saves and passes per team
  - Both the request message and the statistics block auto-delete after 1 minute

### Match Statistics
- Added `MatchStatistics` and `TeamStatistics` models to `Match`
- Added `FetchMatchStatisticsAsync` to `FootballApiClient`

### Announcements
- Split `AnnouncementService` into focused sub-services: `GoalAnnouncementService`, `CardAnnouncementService`, `DiscordAnnouncementService`
- Moved all announcement files to `Services/Announcements/` subfolder

### New Services
- `CouponPercentageService` — fetches and caches Svenska Spel betting percentages, displayed in the dashboard
- `CouponEventSyncService` — synchronizes coupon events from external source
- `ApiUsageTracker` — tracks and logs Football API usage
- `AnnouncementEventKeys` — centralised constants for announcement event deduplication

### New Models
- `CouponEvent` — model representing a tracked match event in the coupon

### Refactoring
- Extracted `DashboardBuilder` from `CouponEvaluator` — formatting logic is now fully separated from evaluation logic
- Renamed `StatusMessageService` → `PlayerMessageService`
- Dashboard column alignment improvements for FT, HT, match minutes and extra time

---

## [2.2.0] - 2026-06-01

### New Services
- Added `DashboardService` for managing the live dashboard message (create, update, refresh on startup)
- Added `StatusMessageService` for personalised flavor text and roast messages per player
- Dashboard message now rotates its extra message on an interval during polling

### Coupon Evaluation
- Extracted `BuildCouponStatusMessage` into `CouponEvaluator` — dashboard formatting centralised in one place
- Dashboard displays current symbol, score, match time and betting percentages per row

### Refactoring
- Major refactor of `AnnouncementService` — cleaner separation of announcement flow
- Major refactor of `ScorePollerService` — improved polling structure and readability

### Other
- Added Topptipset helper script for William

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
