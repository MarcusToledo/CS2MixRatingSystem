# CS2 Mix Rating System - Web Integration Contract

**Document Version:** 1.0.0 (aligned with database version 4)  
**Target Database Version:** `PRAGMA user_version = 4`

This document details the database schema and data format contract between the CS2 server plugin and the external web platform.

---

## 1. Key Integration Rules

> [!IMPORTANT]
> **SteamID64 Data Type:**  
> Always treat SteamID64 as a string (`TEXT`) in database mappings and JSON payloads. Using 64-bit integers directly in JavaScript or certain API layers can result in IEEE 754 precision loss, leading to corrupted player identities (e.g., trailing digits rounded to zero).

> [!NOTE]
> **Separation of Concerns:**  
> The plugin acts as the system of record for rating calculations and match performance statistics. Rating tier thresholds (e.g., Bronze, Silver, Gold rank badges) are **not** managed by the plugin. The web platform is responsible for interpreting the rating numbers and defining levels/tiers.

---

## 2. SQLite Schema Specification

Below is the list of database tables exposed for platform integration.

### `players`
Lifetime accumulated statistics and current rating for each player.
- `steamid` (`TEXT`, PRIMARY KEY): Player's SteamID64.
- `name` (`TEXT`): Last known player profile name.
- `rating` (`INTEGER`): Current lifetime Elo rating.
- `matches` (`INTEGER`): Total matches played.
- `wins` / `losses` (`INTEGER`): Accumulated count of wins and losses.
- `kills` / `deaths` / `assists` / `damage` / `mvps` (`INTEGER`/`INTEGER`/`INTEGER`/`INTEGER`/`INTEGER`): Accumulated in-game statistics.
- `created_at` / `updated_at` (`TEXT`): SQLite default timestamps.

### `seasons`
Metadata for game seasons.
- `id` (`INTEGER`, PRIMARY KEY AUTOINCREMENT): Unique identifier.
- `name` (`TEXT`): Name of the season (e.g., "Season 1").
- `started_at` (`TEXT`): Start date/time.
- `ended_at` (`TEXT`, NULL): End date/time (NULL if currently active).
- `is_active` (`INTEGER`): Boolean flag (1 = active, 0 = inactive). Only one season can be active at a time.

### `season_ratings`
A derived ledger of ratings scoped by season.
- `season_id` (`INTEGER`): References `seasons(id)`.
- `steamid` (`TEXT`): References `players(steamid)`.
- `rating` (`INTEGER`): Elo rating in this season (starts at `InitialRating` at season start, follows lifetime delta changes).
- `matches` / `wins` / `losses` (`INTEGER`): Scoped season statistics.
- `updated_at` (`TEXT`): Last update timestamp.
*Composite Primary Key:* `(season_id, steamid)`

### `matches`
Record of completed matches.
- `id` (`INTEGER`, PRIMARY KEY AUTOINCREMENT): Unique match identifier.
- `match_guid` (`TEXT`): UUID string representing the match.
- `map` (`TEXT`): Map name (e.g., `de_mirage`).
- `winner_team` (`INTEGER`): Winner side (2 = Terrorists, 3 = CTs).
- `ct_score` / `t_score` (`INTEGER`): Scores per team.
- `finished_at` (`TEXT`): Match end timestamp.
- `season_id` (`INTEGER`): References `seasons(id)`.

### `rating_history`
Granular record of all rating changes per player per match.
- `id` (`INTEGER`, PRIMARY KEY AUTOINCREMENT): Record ID.
- `match_id` (`INTEGER`): References `matches(id)`.
- `steamid` (`TEXT`): References `players(steamid)`.
- `old_rating` (`INTEGER`): Rating before the match.
- `base_change` (`INTEGER`): Pure Elo delta from win/loss expectation.
- `performance_swing` (`INTEGER`): Individual stats adjustment delta.
- `total_change` (`INTEGER`): Total delta applied (`base_change` + `performance_swing`).
- `new_rating` (`INTEGER`): Rating after the match.
- `season_id` (`INTEGER`): References `seasons(id)`.
- `k_factor_used` (`INTEGER`, NULL): The effective K-factor value applied (e.g., `100` during placement, `50` normally).

### `match_player_stats`
Detailed player performance metrics saved for every match.
- `id` (`INTEGER`, PRIMARY KEY AUTOINCREMENT): Record ID.
- `match_id` (`INTEGER`): References `matches(id)`.
- `steamid` (`TEXT`): References `players(steamid)`.
- `team` (`INTEGER`): Player team during the match (2 = T, 3 = CT).
- `kills` / `deaths` / `assists` / `damage` (`INTEGER`): Match statistics.
- `rounds_played` / `rounds_survived` / `rounds_with_kill` (`INTEGER`): Round metrics.
- `mvps` / `opening_kills` / `opening_deaths` / `trade_kills` / `flash_assists` (`INTEGER`): Advanced performance statistics.
- `clutches_won` / `headshots` / `hs_percent` (always `0`/`0`/`0.0`): Placeholders for future feature phases.
- `adr` / `kd_ratio` / `kast_percent` (`REAL`): Match averages and ratios.
- `abandoned` (`INTEGER`): 1 if the player abandoned mid-match, 0 otherwise.
- `season_id` (`INTEGER`): References `seasons(id)`.

### `admin_audit_log`
Audit trail of all administrative rating alterations.
- `id` (`INTEGER`, PRIMARY KEY AUTOINCREMENT): Unique ID.
- `action` (`TEXT`): The command run (`SET`, `RESET`, `ADD`, `REMOVE`, `WIPE`).
- `admin_steamid` (`TEXT`, NULL): The SteamID64 of the admin running the command (NULL if console).
- `admin_name` (`TEXT`): Profile name of the admin (defaults to `'CONSOLE'`).
- `target_steamid` (`TEXT`, NULL): The target player's SteamID64 (NULL for `WIPE`).
- `old_value` (`INTEGER`, NULL): Previous rating before change.
- `new_value` (`INTEGER`, NULL): New rating set.
- `reason` (`TEXT`, NULL): Optional justification entered by the admin.
- `created_at` (`TEXT`): Time the administrative action occurred.

---

## 3. Date and Time Format Inconsistencies

> [!WARNING]
> Please note the following difference in timestamp formats when querying tables:
> - **Matches table (`matches.finished_at`):** Uses ISO-8601 format with timezone offset (e.g. `"2026-07-31T18:01:10.0000000Z"`).
> - **Other tables (`created_at`, `updated_at`):** Uses SQLite default native UTC timestamps (e.g. `"2026-07-31 18:01:10"` without `T`, `Z`, or fractional seconds).
>
> Parsing code on the web backend must be flexible enough to handle both patterns.
