# CS2 Mix Rating System - Web Integration Contract

**Document Version:** 1.1.0 (schema SQLite abaixo alinhado à versão 4; o banco está agora na versão 5, mas as tabelas novas são detalhe interno — ver seção 4)  
**Target Database Version:** `PRAGMA user_version = 4` (apenas para a seção 2; ver seção 4 para o caminho ativo de integração)

This document details the database schema and data format contract between the CS2 server plugin and the external web platform.

> [!NOTE]
> **Status da integração:** o caminho de leitura direta do SQLite descrito na seção 2 é
> mantido como referência de schema, mas **não é mais o mecanismo ativo de integração**
> — a topologia de produção (servidor de jogo e plataforma web em máquinas diferentes,
> sem filesystem compartilhado) o inviabiliza. A integração ativa é o sync HTTP outbound
> descrito na seção 4.

> [!IMPORTANT]
> **Armazenamento ativo:** a partir desta versão, o plugin não usa mais SQLite local —
> os dados descritos na seção 2 (schema) vivem num projeto Supabase (Postgres) dedicado,
> acessado via PostgREST. O schema Postgres e o contrato de funções RPC estão em
> `docs/superpowers/specs/2026-08-02-supabase-persistence-migration-design.md`. A seção 2
> abaixo permanece útil como referência do MODELO de dados (nomes de tabela/coluna são os
> mesmos), mas o SGBD e o mecanismo de acesso mudaram.

---

## 1. Key Integration Rules

> [!IMPORTANT]
> **SteamID64 Data Type:**  
> Always treat SteamID64 as a string (`TEXT`) in database mappings and JSON payloads. Using 64-bit integers directly in JavaScript or certain API layers can result in IEEE 754 precision loss, leading to corrupted player identities (e.g., trailing digits rounded to zero).

> [!NOTE]
> **Separation of Concerns:**  
> The plugin acts as the system of record for rating calculations and match performance statistics. Rating tier thresholds (e.g., Bronze, Silver, Gold rank badges) are **not** managed by the plugin. The web platform is responsible for interpreting the rating numbers and defining levels/tiers.

> [!IMPORTANT]
> **Read-only web access:**  
> The plugin is the system of record. The web platform must treat the replicated SQLite data as read-only and must not directly modify ratings, statistics, seasons, or match records.

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
A materialized per-season rating aggregate. A player's row is created on their first recorded match in the season, starting from `InitialRating` plus the first match delta. Subsequent match deltas are accumulated in this table.

Administrative rating changes (`SET`, `RESET`, `ADD`, or `REMOVE`) affect the global rating in `players`, but are not automatically reflected in `season_ratings`.

- `season_id` (`INTEGER`): References `seasons(id)`.
- `steamid` (`TEXT`): References `players(steamid)`.
- `rating` (`INTEGER`): Elo rating in this season.
- `matches` / `wins` / `losses` (`INTEGER`): Scoped season statistics.
- `updated_at` (`TEXT`): Last update timestamp.
*Composite Primary Key:* `(season_id, steamid)`

### `matches`
Record of completed matches.
- `id` (`INTEGER`, PRIMARY KEY AUTOINCREMENT): Unique match identifier.
- `match_guid` (`TEXT`): Application-generated UUID string representing the match. It is indexed, but database-level uniqueness is not currently enforced.
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
- `performance_swing` (`INTEGER`): Individual performance adjustment. If the player abandoned the match, the configured abandonment penalty is also subtracted from this value.
- `total_change` (`INTEGER`): Actual rating delta applied (`new_rating - old_rating`). Normally equals `base_change + performance_swing`, but may differ when the minimum-rating floor is applied.
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

> [!NOTE]
> **`SET` / `RESET` / `ADD` / `REMOVE` and `rating_history`:**  
> These four actions are recorded only in `admin_audit_log`. They do not generate entries in `rating_history` and do not update `season_ratings`. Therefore, the current value in `players.rating` may not always equal the last `rating_history.new_rating`.

> [!WARNING]
> **`WIPE` is destructive, not just unrecorded:**  
> Unlike the four actions above, `WIPE` deletes all rows from `rating_history`, `match_player_stats`, `season_ratings`, and `matches`, and clears every player from `players` (not just the `rating` column). A replicated copy of this database must treat a `WIPE` audit entry as a signal to clear those tables too, not merely as an unreflected change.

---

## 3. Date and Time Formats

Timestamp columns are stored as `TEXT`, and the database schema does not enforce a single format.

Values normally written by the current plugin use:

- `matches.finished_at`: .NET round-trip ISO-8601 UTC format, such as `"2026-07-31T18:01:10.0000000Z"`.
- SQLite-generated timestamps: UTC format such as `"2026-07-31 18:01:10"`.

The web backend must accept both formats and interpret SQLite `datetime('now')` values as UTC, not as server-local time.

---

## 4. Outbound Web Sync (HTTP) — Rating e Nível

> [!IMPORTANT]
> **Este é o mecanismo ativo de integração.** A seção 2 (schema SQLite) é mantida como
> referência, mas a plataforma web não tem acesso direto ao arquivo do banco.

O plugin envia periodicamente, via `POST` HTTPS de saída, o estado atual de
rating/desempenho dos jogadores marcados como alterados desde o último envio. Cada
envio representa o **estado atual**, não um evento — reenviar um lote já processado é
sempre seguro (idempotente).

### Endpoint

- Método: `POST`
- URL: configurável no plugin (`WebSyncUrl`), deve ser `https://`.
- Autenticação: header `Authorization: Bearer <WebSyncApiKey>`.
- Timeout do lado do plugin: 5 segundos.

### Payload — lote de jogadores

```json
{
  "players": [
    {
      "steamid": "76561198000000000",
      "name": "player_name",
      "rating": 1234,
      "matches": 42,
      "wins": 25,
      "losses": 17,
      "kills": 512,
      "deaths": 430,
      "assists": 88,
      "damage": 98765,
      "mvps": 12,
      "created_at": "2026-01-15T10:00:00Z"
    }
  ]
}
```

### Payload — sinal de wipe

Quando `!rating_wipe` é executado no servidor, o próximo envio é este payload isolado,
em vez do lote normal:

```json
{ "wipe": true }
```

A plataforma deve tratar `{"wipe": true}` como instrução para apagar todos os
registros de rating/nível que mantém para este servidor.

### Contrato de resposta

- `200 OK` = lote inteiro (ou sinal de wipe) aceito. Sem semântica de aceitação
  parcial por item.
- Qualquer outro status = tratado como falha inteira; o plugin reenvia integralmente
  no próximo ciclo (sem backoff exponencial — intervalo fixo do timer).
- A plataforma pode sobrescrever seus registros pelo `steamid` sem necessidade de
  deduplicação ou controle de sequência — o remetente garante idempotência.
