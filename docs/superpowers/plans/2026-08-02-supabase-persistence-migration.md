# Supabase Persistence Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the local SQLite-backed `DatabaseService` with a Supabase-backed `SupabaseDatabaseService` (via PostgREST HTTP API), so the plugin no longer depends on any storage on the game server host — which reverts the plugin's data folder to a stale snapshot on every restart.

**Architecture:** Extract `IDatabaseService` (23 methods, same signatures as today's `DatabaseService`). `RatingService`, `PlayerService`, `WebSyncService`, `AdminCommands` depend on the interface, not the concrete class. Two implementations: `FakeDatabaseService` (in-memory, test-only) and `SupabaseDatabaseService` (production, HTTP calls to PostgREST — plain REST for single-table CRUD, Postgres RPC functions for the 5 operations that need atomic multi-table writes plus 1 read-only aggregate). Full design rationale, schema, and RPC contract: `docs/superpowers/specs/2026-08-02-supabase-persistence-migration-design.md`.

**Tech Stack:** C# / .NET 8, `System.Net.Http.Json`, `System.Text.Json`, xUnit.

## Global Constraints

- SteamID64 is always `string` end-to-end (never a raw numeric type in JSON/DB mappings) — matches `docs/integration-contract.md` rule and the existing codebase convention.
- HTTP timeout for all Supabase calls: 5 seconds (same value already used by `HttpWebSyncClient`).
- `WriteMatchEndResultAsync` retries up to 3 attempts with exponential backoff (1s, then 2s) before letting the exception propagate — it is the most valuable write in the system (in-memory match stats are gone once this fails for good).
- Never log the Supabase service key, in any log level, including error paths.
- `WebSyncService` / `HttpWebSyncClient` / `IWebSyncClient` are NOT touched by this plan — they keep working exactly as they do today, just against data that now lives in Supabase instead of SQLite.
- No SQL, no `Microsoft.Data.Sqlite`, remains anywhere in the main project or the test project after this plan is complete.

---

### Task 1: `IDatabaseService` interface + Supabase config fields

**Files:**
- Create: `Database/IDatabaseService.cs`
- Modify: `Config/RankingConfig.cs`

**Interfaces:**
- Produces: `IDatabaseService` — the contract every later task implements or depends on. Exact signatures below are final; no later task may change them.

- [ ] **Step 1: Create the interface**

```csharp
// Database/IDatabaseService.cs
using MixRanking.Models;

namespace MixRanking.Database;

/// <summary>Contrato de persistência do plugin. Implementações: SupabaseDatabaseService (produção), FakeDatabaseService (teste).</summary>
public interface IDatabaseService
{
    Task InitializeAsync();

    Task<PlayerData> GetOrCreatePlayerAsync(string steamId, string name, int initialRating);
    Task<PlayerData?> GetPlayerAsync(string steamId);
    Task<List<PlayerData>> GetTopPlayersAsync(int count = 10);
    Task<int> GetPlayerRankPositionAsync(string steamId);
    Task<int> GetTotalRankedPlayersAsync();
    Task<int> GetPlayerTotalRoundsPlayedAsync(string steamId);
    Task<Dictionary<string, PlayerData>> GetPlayersBySteamIdsAsync(List<string> steamIds);

    Task<int> GetActiveSeasonIdAsync();

    Task<RatingChange?> GetLastRatingChangeAsync(string steamId);
    Task<List<RatingChange>> GetRatingHistoryAsync(string steamId, int count = 10);
    Task<MatchRecord?> GetMatchByIdAsync(long matchId);

    Task UpsertWebSyncQueueAsync(PlayerData player);
    Task<List<PlayerData>> GetPendingWebSyncEntriesAsync(int limit);
    Task ClearWebSyncQueueEntriesAsync(List<string> steamIds);
    Task<bool> IsWipePendingAsync();
    Task SetWipePendingAsync();
    Task ClearWipePendingAsync();

    Task WriteMatchEndResultAsync(MatchRecord match, List<MatchPlayerUpdate> updates, int initialRating, int activeSeasonId);
    Task SetPlayerRatingWithAuditAsync(string targetSteamId, int newRating, string? adminSteamId, string adminName, string? reason);
    Task ResetPlayerWithAuditAsync(string targetSteamId, int initialRating, string? adminSteamId, string adminName, string? reason);
    Task AdjustPlayerRatingWithAuditAsync(string targetSteamId, int amount, bool isAdd, int minRating, string? adminSteamId, string adminName, string? reason);
    Task ResetAllDataWithAuditAsync(string? adminSteamId, string adminName, string? reason);
}
```

- [ ] **Step 2: Add Supabase fields to `RankingConfig`**

In `Config/RankingConfig.cs`, add these two properties near the other connection-related fields (after `MinRating`, before `PlacementMatchCount` is fine):

```csharp
    /// <summary>URL base do projeto Supabase dedicado ao plugin (ex: https://xxxx.supabase.co).</summary>
    [JsonPropertyName("SupabaseUrl")]
    public string SupabaseUrl { get; set; } = "";

    /// <summary>Service role key do projeto Supabase, enviada nos headers apikey/Authorization.</summary>
    [JsonPropertyName("SupabaseServiceKey")]
    public string SupabaseServiceKey { get; set; } = "";
```

- [ ] **Step 3: Build to confirm no compile errors**

Run: `dotnet build`
Expected: `Compilação com êxito` — the interface and config additions don't reference anything unresolved.

- [ ] **Step 4: Commit**

```bash
git add Database/IDatabaseService.cs Config/RankingConfig.cs
git commit -m "feat: add IDatabaseService interface and Supabase config fields"
```

---

### Task 2: `FakeDatabaseService` + migrate existing tests off SQLite

**Files:**
- Create: `MixRanking.Tests/FakeDatabaseService.cs`
- Modify: `MixRanking.Tests/RatingTests.cs`
- Modify: `MixRanking.Tests/WebSyncServiceTests.cs`

**Interfaces:**
- Consumes: `IDatabaseService` (Task 1).
- Produces: `FakeDatabaseService` — public mutable collections (`Players`, `Matches`, `RatingHistory`, `WebSyncQueue`, `WipePending`, `AdminAuditLog`) that tests can seed/inspect directly, replacing the raw SQL seeding (`INSERT INTO players ...`) used today.

This task has no "write the failing test first" step in the usual sense — the failing state is that `RatingTests.cs`/`WebSyncServiceTests.cs` won't compile once `FakeDatabaseService` doesn't exist yet and the SQLite setup is removed. The existing assertions in those two files ARE the spec `FakeDatabaseService` must satisfy.

- [ ] **Step 1: Create `FakeDatabaseService.cs`**

```csharp
// MixRanking.Tests/FakeDatabaseService.cs
using MixRanking.Database;
using MixRanking.Models;

namespace MixRanking.Tests;

/// <summary>Fake em memória de IDatabaseService, reutilizado pelos testes que hoje usam SQLite em memória.</summary>
internal class FakeDatabaseService : IDatabaseService
{
    public Dictionary<string, PlayerData> Players { get; } = new();
    public List<MatchRecord> Matches { get; } = new();
    public List<RatingChange> RatingHistory { get; } = new();
    public Dictionary<string, PlayerData> WebSyncQueue { get; } = new();
    public bool WipePending { get; set; }
    public List<AdminAuditEntry> AdminAuditLog { get; } = new();

    private readonly List<(string SteamId, int RoundsPlayed)> _roundsPlayedLog = new();
    private long _nextMatchId = 1;
    private long _nextRatingHistoryId = 1;

    public record AdminAuditEntry(string Action, string? AdminSteamId, string AdminName, string? TargetSteamId, int? OldValue, int? NewValue, string? Reason);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<PlayerData> GetOrCreatePlayerAsync(string steamId, string name, int initialRating)
    {
        if (Players.TryGetValue(steamId, out var existing))
        {
            existing.Name = name;
            return Task.FromResult(Clone(existing));
        }
        var player = new PlayerData { SteamId = steamId, Name = name, Rating = initialRating };
        Players[steamId] = player;
        return Task.FromResult(Clone(player));
    }

    public Task<PlayerData?> GetPlayerAsync(string steamId)
        => Task.FromResult(Players.TryGetValue(steamId, out var p) ? Clone(p) : null);

    public Task<List<PlayerData>> GetTopPlayersAsync(int count = 10)
    {
        var top = Players.Values.Where(p => p.Matches > 0)
            .OrderByDescending(p => p.Rating)
            .Take(count)
            .Select(Clone)
            .ToList();
        return Task.FromResult(top);
    }

    public Task<int> GetPlayerRankPositionAsync(string steamId)
    {
        int targetRating = Players.TryGetValue(steamId, out var p) ? p.Rating : 0;
        int position = Players.Values.Count(x => x.Matches > 0 && x.Rating > targetRating) + 1;
        return Task.FromResult(position);
    }

    public Task<int> GetTotalRankedPlayersAsync()
        => Task.FromResult(Players.Values.Count(p => p.Matches > 0));

    public Task<int> GetPlayerTotalRoundsPlayedAsync(string steamId)
        => Task.FromResult(_roundsPlayedLog.Where(r => r.SteamId == steamId).Sum(r => r.RoundsPlayed));

    public Task<Dictionary<string, PlayerData>> GetPlayersBySteamIdsAsync(List<string> steamIds)
    {
        var result = new Dictionary<string, PlayerData>();
        foreach (var id in steamIds)
        {
            if (Players.TryGetValue(id, out var p)) result[id] = Clone(p);
        }
        return Task.FromResult(result);
    }

    public Task<int> GetActiveSeasonIdAsync() => Task.FromResult(1);

    public Task<RatingChange?> GetLastRatingChangeAsync(string steamId)
    {
        var change = RatingHistory.Where(r => r.SteamId == steamId).OrderByDescending(r => r.Id).FirstOrDefault();
        return Task.FromResult(change == null ? null : Clone(change));
    }

    public Task<List<RatingChange>> GetRatingHistoryAsync(string steamId, int count = 10)
    {
        var changes = RatingHistory.Where(r => r.SteamId == steamId)
            .OrderByDescending(r => r.Id)
            .Take(count)
            .Select(Clone)
            .ToList();
        return Task.FromResult(changes);
    }

    public Task<MatchRecord?> GetMatchByIdAsync(long matchId)
        => Task.FromResult(Matches.FirstOrDefault(m => m.Id == matchId));

    public Task UpsertWebSyncQueueAsync(PlayerData player)
    {
        WebSyncQueue[player.SteamId] = Clone(player);
        return Task.CompletedTask;
    }

    public Task<List<PlayerData>> GetPendingWebSyncEntriesAsync(int limit)
        => Task.FromResult(WebSyncQueue.Values.Take(limit).Select(Clone).ToList());

    public Task ClearWebSyncQueueEntriesAsync(List<string> steamIds)
    {
        foreach (var id in steamIds) WebSyncQueue.Remove(id);
        return Task.CompletedTask;
    }

    public Task<bool> IsWipePendingAsync() => Task.FromResult(WipePending);
    public Task SetWipePendingAsync() { WipePending = true; return Task.CompletedTask; }
    public Task ClearWipePendingAsync() { WipePending = false; return Task.CompletedTask; }

    public Task WriteMatchEndResultAsync(MatchRecord match, List<MatchPlayerUpdate> updates, int initialRating, int activeSeasonId)
    {
        long matchId = _nextMatchId++;
        match.Id = matchId;
        Matches.Add(match);

        foreach (var update in updates)
        {
            var stats = update.Stats;
            var playerData = update.PlayerData;
            var ratingChange = update.RatingChange;
            ratingChange.MatchId = matchId;

            if (!Players.TryGetValue(playerData.SteamId, out var player))
            {
                player = new PlayerData { SteamId = playerData.SteamId, Name = stats.PlayerName, Rating = playerData.Rating };
                Players[playerData.SteamId] = player;
            }
            else
            {
                player.Name = stats.PlayerName;
            }

            player.Rating = update.NewRating;
            player.Matches += 1;
            if (update.Won) player.Wins += 1; else player.Losses += 1;
            player.Kills += stats.Kills;
            player.Deaths += stats.Deaths;
            player.Assists += stats.Assists;
            player.Damage += stats.Damage;
            player.Mvps += stats.Mvps;
            player.UpdatedAt = DateTime.UtcNow;

            // Espelha o schema real: rating_history não persiste PlayerName/Won (só existem em memória pro chat).
            RatingHistory.Add(new RatingChange
            {
                Id = _nextRatingHistoryId++,
                MatchId = matchId,
                SteamId = ratingChange.SteamId,
                OldRating = ratingChange.OldRating,
                BaseChange = ratingChange.BaseChange,
                PerformanceSwing = ratingChange.PerformanceSwing,
                TotalChange = ratingChange.TotalChange,
                NewRating = ratingChange.NewRating,
                KFactorUsed = ratingChange.KFactorUsed
            });

            _roundsPlayedLog.Add((playerData.SteamId, stats.RoundsPlayed));
        }

        return Task.CompletedTask;
    }

    public Task SetPlayerRatingWithAuditAsync(string targetSteamId, int newRating, string? adminSteamId, string adminName, string? reason)
    {
        if (!Players.TryGetValue(targetSteamId, out var player)) throw new Exception("Jogador não encontrado.");
        int oldRating = player.Rating;
        player.Rating = newRating;
        AdminAuditLog.Add(new AdminAuditEntry("SET", adminSteamId, adminName, targetSteamId, oldRating, newRating, reason));
        return Task.CompletedTask;
    }

    public Task ResetPlayerWithAuditAsync(string targetSteamId, int initialRating, string? adminSteamId, string adminName, string? reason)
    {
        if (!Players.TryGetValue(targetSteamId, out var player)) throw new Exception("Jogador não encontrado.");
        int oldRating = player.Rating;
        player.Rating = initialRating;
        player.Matches = 0; player.Wins = 0; player.Losses = 0;
        player.Kills = 0; player.Deaths = 0; player.Assists = 0; player.Damage = 0; player.Mvps = 0;
        AdminAuditLog.Add(new AdminAuditEntry("RESET", adminSteamId, adminName, targetSteamId, oldRating, initialRating, reason));
        return Task.CompletedTask;
    }

    public Task AdjustPlayerRatingWithAuditAsync(string targetSteamId, int amount, bool isAdd, int minRating, string? adminSteamId, string adminName, string? reason)
    {
        if (!Players.TryGetValue(targetSteamId, out var player)) throw new Exception("Jogador não encontrado.");
        int oldRating = player.Rating;
        int newRating = isAdd ? oldRating + amount : Math.Max(oldRating - amount, minRating);
        player.Rating = newRating;
        AdminAuditLog.Add(new AdminAuditEntry(isAdd ? "ADD" : "REMOVE", adminSteamId, adminName, targetSteamId, oldRating, newRating, reason));
        return Task.CompletedTask;
    }

    public Task ResetAllDataWithAuditAsync(string? adminSteamId, string adminName, string? reason)
    {
        AdminAuditLog.Add(new AdminAuditEntry("WIPE", adminSteamId, adminName, null, null, null, reason));
        Players.Clear();
        Matches.Clear();
        RatingHistory.Clear();
        WebSyncQueue.Clear();
        _roundsPlayedLog.Clear();
        WipePending = true;
        return Task.CompletedTask;
    }

    private static PlayerData Clone(PlayerData p) => new()
    {
        SteamId = p.SteamId, Name = p.Name, Rating = p.Rating, Matches = p.Matches,
        Wins = p.Wins, Losses = p.Losses, Kills = p.Kills, Deaths = p.Deaths,
        Assists = p.Assists, Damage = p.Damage, Mvps = p.Mvps,
        CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt
    };

    private static RatingChange Clone(RatingChange r) => new()
    {
        Id = r.Id, MatchId = r.MatchId, SteamId = r.SteamId, OldRating = r.OldRating,
        BaseChange = r.BaseChange, PerformanceSwing = r.PerformanceSwing,
        TotalChange = r.TotalChange, NewRating = r.NewRating, KFactorUsed = r.KFactorUsed
    };
}
```

- [ ] **Step 2: Migrate `RatingTests.cs` off SQLite**

Replace the `using`s, fields, and constructor:

```csharp
// Replace these usings:
using Microsoft.Extensions.Logging.Abstractions;
using MixRanking.Config;
using MixRanking.Models;
using MixRanking.Services;
using CounterStrikeSharp.API.Modules.Utils;
using Xunit;

// (drop `using Microsoft.Data.Sqlite;` and `using MixRanking.Database;`)

namespace MixRanking.Tests;

public class RatingTests
{
    private readonly FakeDatabaseService _db;
    private readonly RankingConfig _config;
    private readonly WebSyncService _webSyncService;
    private readonly RatingService _ratingService;

    public RatingTests()
    {
        _db = new FakeDatabaseService();

        _config = new RankingConfig
        {
            InitialRating = 1000,
            KFactor = 50,
            PlacementMatchCount = 10,
            PlacementKFactorMultiplier = 2.0,
            MinRating = 100,
            MaxSwing = 7,
            AbandonPenalty = 15
        };

        _webSyncService = new WebSyncService(_db, new FakeWebSyncClient(), NullLogger.Instance);
        _ratingService = new RatingService(_db, _config, _webSyncService, NullLogger.Instance);
    }

    // ... keep every [Fact] method body as-is below this point ...
```

Class no longer implements `IDisposable` (no connection to keep alive) — remove `: IDisposable` and the `Dispose()` method.

In `ProcessMatchEndAsync_AppliesStandardKFactorAfterPlacement`, replace the raw-SQL seeding block:

```csharp
// Replace this:
using (var connection = new SqliteConnection(ConnectionString))
{
    await connection.OpenAsync();
    using var command = connection.CreateCommand();
    command.CommandText = @"
        INSERT INTO players (steamid, name, rating, matches, wins, losses)
        VALUES ('76561198000000200', 'ExperiencedPlayer', 1200, 10, 5, 5);";
    await command.ExecuteNonQueryAsync();
}

// With this:
_db.Players["76561198000000200"] = new PlayerData
{
    SteamId = "76561198000000200", Name = "ExperiencedPlayer",
    Rating = 1200, Matches = 10, Wins = 5, Losses = 5
};
```

Also remove the two `await _db.InitializeAsync();` calls at the top of each `[Fact]` (still valid to keep — `FakeDatabaseService.InitializeAsync()` is a no-op — but they're dead weight; removing is optional cleanup, not required for tests to pass). Leave them if simpler — they no-op harmlessly.

- [ ] **Step 3: Migrate `WebSyncServiceTests.cs` off SQLite**

```csharp
// Replace these usings:
using Microsoft.Extensions.Logging.Abstractions;
using MixRanking.Models;
using MixRanking.Services;
using Xunit;

// (drop `using Microsoft.Data.Sqlite;` and `using MixRanking.Database;`)

namespace MixRanking.Tests;

/// <summary>Fake de IWebSyncClient reutilizado por WebSyncServiceTests e RatingTests.</summary>
internal class FakeWebSyncClient : IWebSyncClient
{
    // ... unchanged ...
}

public class WebSyncServiceTests
{
    private readonly FakeDatabaseService _db;

    public WebSyncServiceTests()
    {
        _db = new FakeDatabaseService();
    }

    // ... keep every [Fact] method body as-is (they only call _db.* and service.* methods
    // that already exist on IDatabaseService — no other changes needed) ...
```

Class no longer implements `IDisposable`; remove `: IDisposable` and the `Dispose()` method. Drop every `await _db.InitializeAsync();` call or leave it (no-op either way).

- [ ] **Step 4: Run the test suite**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj`
Expected: `RatingTests` (3 tests) and `WebSyncServiceTests` (4 tests) all pass. `DatabaseTests.cs` still references the real `DatabaseService`/SQLite and is untouched for now — it still passes too (removed in Task 4).

- [ ] **Step 5: Commit**

```bash
git add MixRanking.Tests/FakeDatabaseService.cs MixRanking.Tests/RatingTests.cs MixRanking.Tests/WebSyncServiceTests.cs
git commit -m "test: add FakeDatabaseService, migrate RatingTests/WebSyncServiceTests off SQLite"
```

---

### Task 3: Wire `IDatabaseService` through the rest of the plugin

**Files:**
- Modify: `Services/RatingService.cs`
- Modify: `Services/PlayerService.cs`
- Modify: `Services/WebSyncService.cs`
- Modify: `Commands/AdminCommands.cs`

**Interfaces:**
- Consumes: `IDatabaseService` (Task 1).

- [ ] **Step 1: Change constructor parameter types**

In each of the four files, change every constructor parameter (and backing field type, if declared) from `DatabaseService` to `IDatabaseService`. Example for `Services/RatingService.cs`:

```csharp
// Before:
private readonly DatabaseService _db;
public RatingService(DatabaseService db, RankingConfig config, WebSyncService webSyncService, ILogger logger)

// After:
private readonly IDatabaseService _db;
public RatingService(IDatabaseService db, RankingConfig config, WebSyncService webSyncService, ILogger logger)
```

Apply the same `DatabaseService` → `IDatabaseService` swap to the field/constructor in `PlayerService.cs`, `WebSyncService.cs`, and `AdminCommands.cs`. No method bodies change — every call site already only used members that now live on `IDatabaseService`.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: fails at this point — `MixRankingPlugin.cs` still constructs a concrete `DatabaseService` and passes it around; that's fine, `DatabaseService` still implements nothing yet (it doesn't implement `IDatabaseService` and won't — it gets deleted in Task 4). This step is expected to show a compile error naming `MixRankingPlugin.cs`; if it points anywhere else, stop and investigate before continuing.

- [ ] **Step 3: Temporarily make `DatabaseService` implement `IDatabaseService`**

This is a bridge step so the plugin compiles between Task 3 and Task 4 (where `DatabaseService` gets deleted and replaced by `SupabaseDatabaseService`). In `Database/DatabaseService.cs`, change the class declaration:

```csharp
// Before:
public class DatabaseService

// After:
public class DatabaseService : IDatabaseService
```

- [ ] **Step 4: Build and run full test suite**

Run: `dotnet build && dotnet test MixRanking.Tests/MixRanking.Tests.csproj`
Expected: build succeeds, all tests pass (same set as end of Task 2, plus `DatabaseTests.cs` still exercising the real SQLite `DatabaseService`).

- [ ] **Step 5: Commit**

```bash
git add Services/RatingService.cs Services/PlayerService.cs Services/WebSyncService.cs Commands/AdminCommands.cs Database/DatabaseService.cs
git commit -m "refactor: depend on IDatabaseService instead of concrete DatabaseService"
```

---

### Task 4: Remove SQLite entirely

**Files:**
- Delete: `Database/DatabaseService.cs`
- Delete: `MixRanking.Tests/DatabaseTests.cs`
- Modify: `MixRanking.csproj`
- Modify: `MixRanking.Tests/MixRanking.Tests.csproj`
- Modify: `MixRankingPlugin.cs`

**Interfaces:**
- Consumes: `IDatabaseService` (Task 1), `FakeDatabaseService` (Task 2).
- Produces: nothing new — this task only removes code. `MixRankingPlugin.cs` is left with a broken `_database` construction line, intentionally, to be completed in Task 9 once `SupabaseDatabaseService` exists.

- [ ] **Step 1: Delete the SQLite implementation and its tests**

```bash
git rm Database/DatabaseService.cs MixRanking.Tests/DatabaseTests.cs
```

- [ ] **Step 2: Remove the `Microsoft.Data.Sqlite` package reference**

In `MixRanking.csproj`, remove this line from the `<ItemGroup>`:

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.0" />
```

In `MixRanking.Tests/MixRanking.Tests.csproj`, remove the same line.

- [ ] **Step 3: Stub out the now-broken construction in `MixRankingPlugin.cs`**

`MixRankingPlugin.Load()` currently has:

```csharp
string dbPath = Path.Combine(ModuleDirectory, "mixranking.db");
_database = new DatabaseService(dbPath);

try
{
    _database.InitializeAsync().GetAwaiter().GetResult();
    Logger.LogInformation("[MixRanking] Database initialized at {Path}", dbPath);
}
catch (Exception ex)
{
    Logger.LogError(ex, "[MixRanking] Failed to initialize database!");
    throw;
}
```

Leave this block as a compile error for now (`DatabaseService` no longer exists) — it gets replaced with `SupabaseDatabaseService` in Task 9. Do not attempt to make the project build at the end of this task; that's expected and resolved by Task 9.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: remove SQLite implementation and dependency (broken build, fixed in next tasks)"
```

This is the one commit in this plan that leaves the build red — acceptable because it's a mechanical removal step immediately followed by Tasks 5–9, which rebuild the missing piece. If you'd rather keep every commit green, squash Tasks 4–9 before pushing; do not skip the intermediate commits while working, they're checkpoints for review.

---

### Task 5: `SupabaseDatabaseService` — scaffolding, DTOs, player methods

**Files:**
- Create: `Database/SupabaseModels.cs`
- Create: `Database/SupabaseDatabaseService.cs`
- Create: `Database/SupabaseDatabaseService.Players.cs`
- Test: `MixRanking.Tests/SupabaseDatabaseServiceTests.cs`

**Interfaces:**
- Consumes: `IDatabaseService` (Task 1), `RankingConfig.SupabaseUrl` / `SupabaseServiceKey` (Task 1).
- Produces: `SupabaseDatabaseService` (partial class, implements `IDatabaseService`) — constructor `SupabaseDatabaseService(RankingConfig config, ILogger logger)` for production, and `SupabaseDatabaseService(RankingConfig config, ILogger logger, HttpMessageHandler handler)` for tests. Later tasks (6–8) add more partial files to this same class; none of them touch the constructor or `SendAsync` defined here.

- [ ] **Step 1: Write the failing test**

```csharp
// MixRanking.Tests/SupabaseDatabaseServiceTests.cs
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MixRanking.Config;
using MixRanking.Database;
using Xunit;

namespace MixRanking.Tests;

internal class FakeHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }
    public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
        return ResponseToReturn;
    }
}

public class SupabaseDatabaseServiceTests
{
    private static RankingConfig MakeConfig() => new()
    {
        SupabaseUrl = "https://fake-project.supabase.co",
        SupabaseServiceKey = "fake-service-key"
    };

    [Fact]
    public async Task GetPlayerAsync_SendsCorrectRequestAndParsesResponse()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"steamid":"76561198000000001","name":"Manu","rating":1050,"matches":1,"wins":1,"losses":0,"kills":20,"deaths":15,"assists":2,"damage":2103,"mvps":2,"created_at":"2026-08-02T23:17:56Z","updated_at":"2026-08-02T23:17:56Z"}]""",
                    Encoding.UTF8, "application/json")
            }
        };
        var db = new SupabaseDatabaseService(MakeConfig(), NullLogger.Instance, handler);

        var player = await db.GetPlayerAsync("76561198000000001");

        Assert.NotNull(player);
        Assert.Equal("Manu", player!.Name);
        Assert.Equal(1050, player.Rating);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("https://fake-project.supabase.co/rest/v1/players?steamid=eq.76561198000000001", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("fake-service-key", handler.LastRequest.Headers.GetValues("apikey").First());
        Assert.Equal("Bearer fake-service-key", handler.LastRequest.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task GetPlayerAsync_ReturnsNull_WhenNoRowsMatch()
    {
        var handler = new FakeHttpMessageHandler();
        var db = new SupabaseDatabaseService(MakeConfig(), NullLogger.Instance, handler);

        var player = await db.GetPlayerAsync("76561198000000999");

        Assert.Null(player);
    }

    [Fact]
    public async Task GetOrCreatePlayerAsync_InsertsNewPlayer_WhenNotFound()
    {
        var handler = new FakeHttpMessageHandler();
        var db = new SupabaseDatabaseService(MakeConfig(), NullLogger.Instance, handler);

        var player = await db.GetOrCreatePlayerAsync("76561198000000002", "NewGuy", 1000);

        Assert.Equal("76561198000000002", player.SteamId);
        Assert.Equal(1000, player.Rating);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("\"steamid\":\"76561198000000002\"", handler.LastRequestBody);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter SupabaseDatabaseServiceTests`
Expected: FAIL — `SupabaseDatabaseService` doesn't exist yet, compile error.

- [ ] **Step 3: Write the DTOs**

```csharp
// Database/SupabaseModels.cs
using System.Text.Json.Serialization;
using MixRanking.Models;

namespace MixRanking.Database;

internal class SupabasePlayerRow
{
    [JsonPropertyName("steamid")] public required string SteamId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("rating")] public int Rating { get; set; }
    [JsonPropertyName("matches")] public int Matches { get; set; }
    [JsonPropertyName("wins")] public int Wins { get; set; }
    [JsonPropertyName("losses")] public int Losses { get; set; }
    [JsonPropertyName("kills")] public int Kills { get; set; }
    [JsonPropertyName("deaths")] public int Deaths { get; set; }
    [JsonPropertyName("assists")] public int Assists { get; set; }
    [JsonPropertyName("damage")] public long Damage { get; set; }
    [JsonPropertyName("mvps")] public int Mvps { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }

    public PlayerData ToPlayerData() => new()
    {
        SteamId = SteamId, Name = Name, Rating = Rating, Matches = Matches,
        Wins = Wins, Losses = Losses, Kills = Kills, Deaths = Deaths,
        Assists = Assists, Damage = Damage, Mvps = Mvps,
        CreatedAt = CreatedAt, UpdatedAt = UpdatedAt
    };
}

internal class SupabaseSeasonIdRow
{
    [JsonPropertyName("id")] public int Id { get; set; }
}

internal class SupabaseMatchRow
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("match_guid")] public string MatchGuid { get; set; } = string.Empty;
    [JsonPropertyName("map")] public string Map { get; set; } = string.Empty;
    [JsonPropertyName("winner_team")] public int WinnerTeam { get; set; }
    [JsonPropertyName("ct_score")] public int CtScore { get; set; }
    [JsonPropertyName("t_score")] public int TScore { get; set; }
    [JsonPropertyName("finished_at")] public DateTime FinishedAt { get; set; }

    public MatchRecord ToMatchRecord() => new()
    {
        Id = Id, MatchGuid = MatchGuid, Map = Map, WinnerTeam = WinnerTeam,
        CtScore = CtScore, TScore = TScore, FinishedAt = FinishedAt
    };
}

internal class SupabaseRatingHistoryRow
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("match_id")] public long MatchId { get; set; }
    [JsonPropertyName("steamid")] public string SteamId { get; set; } = string.Empty;
    [JsonPropertyName("old_rating")] public int OldRating { get; set; }
    [JsonPropertyName("base_change")] public int BaseChange { get; set; }
    [JsonPropertyName("performance_swing")] public int PerformanceSwing { get; set; }
    [JsonPropertyName("total_change")] public int TotalChange { get; set; }
    [JsonPropertyName("new_rating")] public int NewRating { get; set; }
    [JsonPropertyName("k_factor_used")] public int? KFactorUsed { get; set; }

    public RatingChange ToRatingChange() => new()
    {
        Id = Id, MatchId = MatchId, SteamId = SteamId, OldRating = OldRating,
        BaseChange = BaseChange, PerformanceSwing = PerformanceSwing,
        TotalChange = TotalChange, NewRating = NewRating, KFactorUsed = KFactorUsed
    };
}

internal class SupabaseWebSyncQueueRow
{
    [JsonPropertyName("steamid")] public required string SteamId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("rating")] public int Rating { get; set; }
    [JsonPropertyName("matches")] public int Matches { get; set; }
    [JsonPropertyName("wins")] public int Wins { get; set; }
    [JsonPropertyName("losses")] public int Losses { get; set; }
    [JsonPropertyName("kills")] public int Kills { get; set; }
    [JsonPropertyName("deaths")] public int Deaths { get; set; }
    [JsonPropertyName("assists")] public int Assists { get; set; }
    [JsonPropertyName("damage")] public long Damage { get; set; }
    [JsonPropertyName("mvps")] public int Mvps { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }

    public PlayerData ToPlayerData() => new()
    {
        SteamId = SteamId, Name = Name, Rating = Rating, Matches = Matches,
        Wins = Wins, Losses = Losses, Kills = Kills, Deaths = Deaths,
        Assists = Assists, Damage = Damage, Mvps = Mvps, CreatedAt = CreatedAt
    };
}

internal class SupabaseWipePendingRow
{
    [JsonPropertyName("wipe_pending")] public bool WipePending { get; set; }
}
```

- [ ] **Step 4: Write the scaffolding (constructor + `SendAsync` + `InitializeAsync`)**

```csharp
// Database/SupabaseDatabaseService.cs
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MixRanking.Config;

namespace MixRanking.Database;

/// <summary>Persistência via PostgREST (Supabase), dedicado a este plugin. Ver docs/superpowers/specs/2026-08-02-supabase-persistence-migration-design.md.</summary>
public partial class SupabaseDatabaseService : IDatabaseService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly RankingConfig _config;
    private readonly ILogger _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SupabaseDatabaseService(RankingConfig config, ILogger logger)
        : this(config, logger, new HttpClientHandler())
    {
    }

    /// <summary>Construtor para injeção de um HttpMessageHandler fake em teste.</summary>
    public SupabaseDatabaseService(RankingConfig config, ILogger logger, HttpMessageHandler handler)
    {
        _config = config;
        _logger = logger;
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task InitializeAsync()
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "players?limit=1", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MixRanking] Failed to reach Supabase at startup — plugin will keep loading, commands will report errors until connectivity is restored.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathAndQuery, object? body, Dictionary<string, string>? extraHeaders = null)
    {
        using var request = new HttpRequestMessage(method, $"{_config.SupabaseUrl}/rest/v1/{pathAndQuery}");
        request.Headers.Add("apikey", _config.SupabaseServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.SupabaseServiceKey);
        if (extraHeaders != null)
        {
            foreach (var (key, value) in extraHeaders) request.Headers.Add(key, value);
        }
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    public void Dispose() => _httpClient.Dispose();
}
```

- [ ] **Step 5: Write the player methods**

```csharp
// Database/SupabaseDatabaseService.Players.cs
using System.Net.Http.Json;
using MixRanking.Models;

namespace MixRanking.Database;

public partial class SupabaseDatabaseService
{
    public async Task<PlayerData> GetOrCreatePlayerAsync(string steamId, string name, int initialRating)
    {
        var existing = await GetPlayerAsync(steamId);
        if (existing != null)
        {
            if (existing.Name != name)
            {
                using var patchResponse = await SendAsync(HttpMethod.Patch, $"players?steamid=eq.{Uri.EscapeDataString(steamId)}", new { name });
                existing.Name = name;
            }
            return existing;
        }

        using var response = await SendAsync(HttpMethod.Post, "players", new { steamid = steamId, name, rating = initialRating });
        return new PlayerData { SteamId = steamId, Name = name, Rating = initialRating };
    }

    public async Task<PlayerData?> GetPlayerAsync(string steamId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"players?steamid=eq.{Uri.EscapeDataString(steamId)}", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabasePlayerRow>>(JsonOptions);
        return rows is { Count: > 0 } ? rows[0].ToPlayerData() : null;
    }

    public async Task<List<PlayerData>> GetTopPlayersAsync(int count = 10)
    {
        using var response = await SendAsync(HttpMethod.Get, $"players?matches=gt.0&order=rating.desc&limit={count}", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabasePlayerRow>>(JsonOptions) ?? new();
        return rows.Select(r => r.ToPlayerData()).ToList();
    }

    public async Task<int> GetPlayerRankPositionAsync(string steamId)
    {
        using var response = await SendAsync(HttpMethod.Get, "players?matches=gt.0&select=steamid,rating&order=rating.desc", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabasePlayerRow>>(JsonOptions) ?? new();
        int targetRating = rows.FirstOrDefault(r => r.SteamId == steamId)?.Rating ?? 0;
        return rows.Count(r => r.Rating > targetRating) + 1;
    }

    public async Task<int> GetTotalRankedPlayersAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "players?matches=gt.0&select=steamid", null,
            new Dictionary<string, string> { ["Prefer"] = "count=exact" });
        if (!response.Content.Headers.TryGetValues("Content-Range", out var values)) return 0;
        string contentRange = values.First();
        string totalPart = contentRange.Split('/').Last();
        return totalPart == "*" ? 0 : int.Parse(totalPart);
    }

    public async Task<Dictionary<string, PlayerData>> GetPlayersBySteamIdsAsync(List<string> steamIds)
    {
        if (steamIds.Count == 0) return new();
        string idList = string.Join(",", steamIds.Select(Uri.EscapeDataString));
        using var response = await SendAsync(HttpMethod.Get, $"players?steamid=in.({idList})", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabasePlayerRow>>(JsonOptions) ?? new();
        return rows.ToDictionary(r => r.SteamId, r => r.ToPlayerData());
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter SupabaseDatabaseServiceTests`
Expected: PASS (3 tests). Note the whole solution still won't build yet (`IDatabaseService` isn't fully implemented — the remaining 17 methods come in Tasks 6–8), so run the filtered test command above, not a full `dotnet build`, until Task 8 is done.

- [ ] **Step 7: Commit**

```bash
git add Database/SupabaseModels.cs Database/SupabaseDatabaseService.cs Database/SupabaseDatabaseService.Players.cs MixRanking.Tests/SupabaseDatabaseServiceTests.cs
git commit -m "feat: add SupabaseDatabaseService scaffolding and player methods"
```

---

### Task 6: `SupabaseDatabaseService` — web sync queue/state methods

**Files:**
- Create: `Database/SupabaseDatabaseService.WebSync.cs`
- Modify: `MixRanking.Tests/SupabaseDatabaseServiceTests.cs`

**Interfaces:**
- Consumes: `SendAsync` helper and `JsonOptions` from Task 5's `SupabaseDatabaseService.cs` (same partial class).

- [ ] **Step 1: Write the failing test**

Append to `MixRanking.Tests/SupabaseDatabaseServiceTests.cs`, inside the `SupabaseDatabaseServiceTests` class:

```csharp
    [Fact]
    public async Task UpsertWebSyncQueueAsync_SendsUpsertWithMergeDuplicatesHeader()
    {
        var handler = new FakeHttpMessageHandler();
        var db = new SupabaseDatabaseService(MakeConfig(), NullLogger.Instance, handler);
        var player = new PlayerData { SteamId = "76561198000000003", Name = "Dirty", Rating = 1200, CreatedAt = DateTime.UtcNow };

        await db.UpsertWebSyncQueueAsync(player);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("resolution=merge-duplicates", handler.LastRequest.Headers.GetValues("Prefer").First());
        Assert.Contains("\"steamid\":\"76561198000000003\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task IsWipePendingAsync_ReturnsTrue_WhenFlagIsSet()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"wipe_pending":true}]""", Encoding.UTF8, "application/json")
            }
        };
        var db = new SupabaseDatabaseService(MakeConfig(), NullLogger.Instance, handler);

        Assert.True(await db.IsWipePendingAsync());
    }
```

Add `using MixRanking.Models;` to the test file's usings if not already present (needed for `PlayerData`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter SupabaseDatabaseServiceTests`
Expected: FAIL — `UpsertWebSyncQueueAsync`/`IsWipePendingAsync` not implemented on `SupabaseDatabaseService` yet.

- [ ] **Step 3: Implement**

```csharp
// Database/SupabaseDatabaseService.WebSync.cs
using System.Net.Http.Json;
using MixRanking.Models;

namespace MixRanking.Database;

public partial class SupabaseDatabaseService
{
    public async Task UpsertWebSyncQueueAsync(PlayerData player)
    {
        var row = new
        {
            steamid = player.SteamId,
            name = player.Name,
            rating = player.Rating,
            matches = player.Matches,
            wins = player.Wins,
            losses = player.Losses,
            kills = player.Kills,
            deaths = player.Deaths,
            assists = player.Assists,
            damage = player.Damage,
            mvps = player.Mvps,
            created_at = player.CreatedAt.ToString("o")
        };
        using var response = await SendAsync(HttpMethod.Post, "web_sync_queue", row,
            new Dictionary<string, string> { ["Prefer"] = "resolution=merge-duplicates" });
    }

    public async Task<List<PlayerData>> GetPendingWebSyncEntriesAsync(int limit)
    {
        using var response = await SendAsync(HttpMethod.Get, $"web_sync_queue?limit={limit}", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseWebSyncQueueRow>>(JsonOptions) ?? new();
        return rows.Select(r => r.ToPlayerData()).ToList();
    }

    public async Task ClearWebSyncQueueEntriesAsync(List<string> steamIds)
    {
        if (steamIds.Count == 0) return;
        string idList = string.Join(",", steamIds.Select(Uri.EscapeDataString));
        using var response = await SendAsync(HttpMethod.Delete, $"web_sync_queue?steamid=in.({idList})", null);
    }

    public async Task<bool> IsWipePendingAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "web_sync_state?id=eq.1&select=wipe_pending", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseWipePendingRow>>(JsonOptions) ?? new();
        return rows.Count > 0 && rows[0].WipePending;
    }

    public async Task SetWipePendingAsync()
    {
        using var response = await SendAsync(HttpMethod.Patch, "web_sync_state?id=eq.1", new { wipe_pending = true });
    }

    public async Task ClearWipePendingAsync()
    {
        using var response = await SendAsync(HttpMethod.Patch, "web_sync_state?id=eq.1", new { wipe_pending = false });
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter SupabaseDatabaseServiceTests`
Expected: PASS (5 tests total so far).

- [ ] **Step 5: Commit**

```bash
git add Database/SupabaseDatabaseService.WebSync.cs MixRanking.Tests/SupabaseDatabaseServiceTests.cs
git commit -m "feat: add SupabaseDatabaseService web sync queue/state methods"
```

---

### Task 7: `SupabaseDatabaseService` — match/rating RPC and read methods

**Files:**
- Create: `Database/SupabaseDatabaseService.Matches.cs`
- Modify: `MixRanking.Tests/SupabaseDatabaseServiceTests.cs`

**Interfaces:**
- Consumes: `SendAsync` helper (Task 5).
- Produces: `WriteMatchEndResultAsync` with the retry behavior required by the Global Constraints section.

- [ ] **Step 1: Write the failing tests**

Append to `SupabaseDatabaseServiceTests.cs`:

```csharp
    [Fact]
    public async Task WriteMatchEndResultAsync_PostsToCorrectRpcEndpoint()
    {
        var handler = new FakeHttpMessageHandler();
        var db = new SupabaseDatabaseService(MakeConfig(), NullLogger.Instance, handler);
        var match = new MatchRecord { MatchGuid = Guid.NewGuid().ToString(), Map = "de_dust2", WinnerTeam = 3, CtScore = 13, TScore = 9 };
        var stats = new MatchPlayerStats { SteamId = 76561198000000004, PlayerName = "Manu", Team = CsTeam.CounterTerrorist, Kills = 20, Deaths = 15, RoundsPlayed = 22 };
        var update = new MatchPlayerUpdate
        {
            Stats = stats,
            PlayerData = new PlayerData { SteamId = "76561198000000004", Name = "Manu", Rating = 1000 },
            RatingChange = new RatingChange { SteamId = "76561198000000004", OldRating = 1000, BaseChange = 30, PerformanceSwing = 5, TotalChange = 35, NewRating = 1035, KFactorUsed = 100 },
            NewRating = 1035,
            Won = true
        };

        await db.WriteMatchEndResultAsync(match, new List<MatchPlayerUpdate> { update }, 1000, 1);

        Assert.Equal("https://fake-project.supabase.co/rest/v1/rpc/write_match_end_result", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"map\":\"de_dust2\"", handler.LastRequestBody);
        Assert.Contains("\"steamid\":\"76561198000000004\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task WriteMatchEndResultAsync_RetriesOnFailureThenSucceeds()
    {
        int callCount = 0;
        var handler = new FakeHttpMessageHandler();
        var db = new SupabaseDatabaseService(MakeConfig(), NullLogger.Instance, new CountingFailThenSucceedHandler(() => callCount++, failuresBeforeSuccess: 1));
        var match = new MatchRecord { MatchGuid = Guid.NewGuid().ToString(), Map = "de_mirage", WinnerTeam = 2, CtScore = 9, TScore = 13 };

        await db.WriteMatchEndResultAsync(match, new List<MatchPlayerUpdate>(), 1000, 1);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetActiveSeasonIdAsync_ReturnsDefaultOne_WhenNoActiveSeasonFound()
    {
        var handler = new FakeHttpMessageHandler();
        var db = new SupabaseDatabaseService(MakeConfig(), NullLogger.Instance, handler);

        Assert.Equal(1, await db.GetActiveSeasonIdAsync());
    }
```

Add this helper class at the bottom of the file (outside `SupabaseDatabaseServiceTests`, alongside `FakeHttpMessageHandler`):

```csharp
internal class CountingFailThenSucceedHandler : HttpMessageHandler
{
    private readonly Action _onCall;
    private readonly int _failuresBeforeSuccess;
    private int _calls;

    public CountingFailThenSucceedHandler(Action onCall, int failuresBeforeSuccess)
    {
        _onCall = onCall;
        _failuresBeforeSuccess = failuresBeforeSuccess;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _onCall();
        _calls++;
        var status = _calls <= _failuresBeforeSuccess ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
    }
}
```

Add `using MixRanking.Models;` and `using CounterStrikeSharp.API.Modules.Utils;` to the test file's usings if not already present.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter SupabaseDatabaseServiceTests`
Expected: FAIL — `WriteMatchEndResultAsync`/`GetActiveSeasonIdAsync` not implemented yet.

- [ ] **Step 3: Implement**

```csharp
// Database/SupabaseDatabaseService.Matches.cs
using System.Net.Http.Json;
using MixRanking.Models;
using MixRanking.Rating;

namespace MixRanking.Database;

public partial class SupabaseDatabaseService
{
    public async Task<int> GetPlayerTotalRoundsPlayedAsync(string steamId)
    {
        using var response = await SendAsync(HttpMethod.Post, "rpc/get_player_total_rounds", new { p_steamid = steamId });
        string json = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(json) || json == "null" ? 0 : int.Parse(json);
    }

    public async Task<int> GetActiveSeasonIdAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "seasons?is_active=eq.true&select=id&limit=1", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseSeasonIdRow>>(JsonOptions) ?? new();
        return rows.Count > 0 ? rows[0].Id : 1;
    }

    public async Task<RatingChange?> GetLastRatingChangeAsync(string steamId)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"rating_history?steamid=eq.{Uri.EscapeDataString(steamId)}&order=id.desc&limit=1", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseRatingHistoryRow>>(JsonOptions) ?? new();
        return rows.Count > 0 ? rows[0].ToRatingChange() : null;
    }

    public async Task<List<RatingChange>> GetRatingHistoryAsync(string steamId, int count = 10)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"rating_history?steamid=eq.{Uri.EscapeDataString(steamId)}&order=id.desc&limit={count}", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseRatingHistoryRow>>(JsonOptions) ?? new();
        return rows.Select(r => r.ToRatingChange()).ToList();
    }

    public async Task<MatchRecord?> GetMatchByIdAsync(long matchId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"matches?id=eq.{matchId}", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseMatchRow>>(JsonOptions) ?? new();
        return rows.Count > 0 ? rows[0].ToMatchRecord() : null;
    }

    public async Task WriteMatchEndResultAsync(MatchRecord match, List<MatchPlayerUpdate> updates, int initialRating, int activeSeasonId)
    {
        var payload = new
        {
            match = new
            {
                match_guid = match.MatchGuid,
                map = match.Map,
                winner_team = match.WinnerTeam,
                ct_score = match.CtScore,
                t_score = match.TScore,
                finished_at = match.FinishedAt.ToString("o"),
                season_id = activeSeasonId
            },
            updates = updates.Select(u => new
            {
                steamid = u.PlayerData.SteamId,
                player_name = u.Stats.PlayerName,
                old_rating = u.RatingChange.OldRating,
                base_change = u.RatingChange.BaseChange,
                performance_swing = u.RatingChange.PerformanceSwing,
                total_change = u.RatingChange.TotalChange,
                new_rating = u.NewRating,
                k_factor_used = u.RatingChange.KFactorUsed,
                won = u.Won,
                team = (int)u.Stats.Team,
                kills = u.Stats.Kills,
                deaths = u.Stats.Deaths,
                assists = u.Stats.Assists,
                damage = u.Stats.Damage,
                rounds_played = u.Stats.RoundsPlayed,
                rounds_survived = u.Stats.RoundsSurvived,
                rounds_with_kill = u.Stats.RoundsWithKill,
                mvps = u.Stats.Mvps,
                opening_kills = u.Stats.OpeningKills,
                opening_deaths = u.Stats.OpeningDeaths,
                trade_kills = u.Stats.TradeKills,
                flash_assists = u.Stats.FlashAssists,
                adr = u.Stats.Adr,
                kd_ratio = u.Stats.KdRatio,
                kast_percent = SwingCalculator.CalculateKastPercent(u.Stats),
                abandoned = u.Stats.Abandoned
            }),
            p_initial_rating = initialRating,
            p_active_season_id = activeSeasonId
        };

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Post, "rpc/write_match_end_result", payload);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                double delaySeconds = Math.Pow(2, attempt - 1);
                _logger.LogWarning(ex, "[MixRanking] write_match_end_result attempt {Attempt}/{Max} failed, retrying in {Delay}s.",
                    attempt, maxAttempts, delaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
        }
    }
}
```

`SwingCalculator.CalculateKastPercent` already exists (used by the old `DatabaseService.WriteMatchEndResultAsync`) — check `Rating/SwingCalculator.cs` for the exact namespace if the `using MixRanking.Rating;` above doesn't resolve it; adjust the `using` to match wherever it actually lives.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter SupabaseDatabaseServiceTests`
Expected: PASS (8 tests total so far).

- [ ] **Step 5: Commit**

```bash
git add Database/SupabaseDatabaseService.Matches.cs MixRanking.Tests/SupabaseDatabaseServiceTests.cs
git commit -m "feat: add SupabaseDatabaseService match/rating-history methods with retry on write"
```

---

### Task 8: `SupabaseDatabaseService` — admin RPC methods

**Files:**
- Create: `Database/SupabaseDatabaseService.Admin.cs`
- Modify: `MixRanking.Tests/SupabaseDatabaseServiceTests.cs`

**Interfaces:**
- Consumes: `SendAsync` helper (Task 5).
- Produces: the last 4 methods of `IDatabaseService` — after this task, `SupabaseDatabaseService` fully implements the interface and the solution builds again.

- [ ] **Step 1: Write the failing test**

Append to `SupabaseDatabaseServiceTests.cs`:

```csharp
    [Fact]
    public async Task ResetAllDataWithAuditAsync_PostsToCorrectRpcEndpointWithReason()
    {
        var handler = new FakeHttpMessageHandler();
        var db = new SupabaseDatabaseService(MakeConfig(), NullLogger.Instance, handler);

        await db.ResetAllDataWithAuditAsync("76561198158106029", "DESACREDITADOS leluia", "reset de teste");

        Assert.Equal("https://fake-project.supabase.co/rest/v1/rpc/reset_all_data", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"p_admin_steamid\":\"76561198158106029\"", handler.LastRequestBody);
        Assert.Contains("\"p_reason\":\"reset de teste\"", handler.LastRequestBody);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter SupabaseDatabaseServiceTests`
Expected: FAIL — `ResetAllDataWithAuditAsync` not implemented yet.

- [ ] **Step 3: Implement**

```csharp
// Database/SupabaseDatabaseService.Admin.cs
namespace MixRanking.Database;

public partial class SupabaseDatabaseService
{
    public async Task SetPlayerRatingWithAuditAsync(string targetSteamId, int newRating, string? adminSteamId, string adminName, string? reason)
    {
        using var response = await SendAsync(HttpMethod.Post, "rpc/set_player_rating", new
        {
            p_target_steamid = targetSteamId,
            p_new_rating = newRating,
            p_admin_steamid = adminSteamId,
            p_admin_name = adminName,
            p_reason = reason
        });
    }

    public async Task ResetPlayerWithAuditAsync(string targetSteamId, int initialRating, string? adminSteamId, string adminName, string? reason)
    {
        using var response = await SendAsync(HttpMethod.Post, "rpc/reset_player", new
        {
            p_target_steamid = targetSteamId,
            p_initial_rating = initialRating,
            p_admin_steamid = adminSteamId,
            p_admin_name = adminName,
            p_reason = reason
        });
    }

    public async Task AdjustPlayerRatingWithAuditAsync(string targetSteamId, int amount, bool isAdd, int minRating, string? adminSteamId, string adminName, string? reason)
    {
        using var response = await SendAsync(HttpMethod.Post, "rpc/adjust_player_rating", new
        {
            p_target_steamid = targetSteamId,
            p_amount = amount,
            p_is_add = isAdd,
            p_min_rating = minRating,
            p_admin_steamid = adminSteamId,
            p_admin_name = adminName,
            p_reason = reason
        });
    }

    public async Task ResetAllDataWithAuditAsync(string? adminSteamId, string adminName, string? reason)
    {
        using var response = await SendAsync(HttpMethod.Post, "rpc/reset_all_data", new
        {
            p_admin_steamid = adminSteamId,
            p_admin_name = adminName,
            p_reason = reason
        });
    }
}
```

- [ ] **Step 4: Run tests, then full build**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter SupabaseDatabaseServiceTests`
Expected: PASS (9 tests total).

Run: `dotnet build`
Expected: still fails — only `MixRankingPlugin.cs` is broken now (Task 4's stub), nothing else. `SupabaseDatabaseService` fully implements `IDatabaseService` as of this step.

- [ ] **Step 5: Commit**

```bash
git add Database/SupabaseDatabaseService.Admin.cs MixRanking.Tests/SupabaseDatabaseServiceTests.cs
git commit -m "feat: add SupabaseDatabaseService admin RPC methods"
```

---

### Task 9: Wire `SupabaseDatabaseService` into the plugin + chat error on save failure

**Files:**
- Modify: `MixRankingPlugin.cs`
- Modify: `Match/MatchEvents.cs`

**Interfaces:**
- Consumes: `SupabaseDatabaseService` (Tasks 5–8).

- [ ] **Step 1: Fix `MixRankingPlugin.Load()`**

Replace the block from Task 4's Step 3:

```csharp
// Before:
string dbPath = Path.Combine(ModuleDirectory, "mixranking.db");
_database = new DatabaseService(dbPath);

try
{
    _database.InitializeAsync().GetAwaiter().GetResult();
    Logger.LogInformation("[MixRanking] Database initialized at {Path}", dbPath);
}
catch (Exception ex)
{
    Logger.LogError(ex, "[MixRanking] Failed to initialize database!");
    throw;
}

// After:
_database = new SupabaseDatabaseService(Config, Logger);
_database.InitializeAsync().GetAwaiter().GetResult();
Logger.LogInformation("[MixRanking] Supabase persistence initialized at {Url}", Config.SupabaseUrl);
```

Change the field declaration from `private DatabaseService _database = null!;` to `private SupabaseDatabaseService _database = null!;` (kept concrete, not `IDatabaseService`, so `Unload()` can call `.Dispose()` on it — see Step 2). Every other service constructed with `_database` (`_ratingService`, `_playerService`, an so on) still compiles unchanged, since `SupabaseDatabaseService` implements `IDatabaseService`.

- [ ] **Step 2: Dispose the HTTP client on unload**

In `MixRankingPlugin.Unload()`:

```csharp
public override void Unload(bool hotReload)
{
    _webSyncClient?.Dispose();
    _database?.Dispose();
    Logger.LogInformation("[MixRanking] Plugin unloaded.");
}
```

- [ ] **Step 3: Add chat-visible error on match-end save failure**

In `Match/MatchEvents.cs`, the `catch` block inside `OnMatchEnd`'s `Task.Run`:

```csharp
// Before:
catch (Exception ex)
{
    _logger.LogError(ex, $"[{_config.ChatPrefix}] Error processing match end.");
}

// After:
catch (Exception ex)
{
    _logger.LogError(ex, $"[{_config.ChatPrefix}] Error processing match end.");
    Server.NextFrame(() =>
        Server.PrintToChatAll($" {ChatColors.Red}[{_config.ChatPrefix}]{ChatColors.Default} Erro ao salvar resultado da partida, contate um admin."));
}
```

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build`
Expected: `Compilação com êxito`.

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj`
Expected: all tests pass (`RatingTests`, `StatisticsServiceTests`, `SwingCalculatorTests`, `WebSyncServiceTests`, `SupabaseDatabaseServiceTests` — `DatabaseTests` no longer exists).

- [ ] **Step 5: Commit**

```bash
git add MixRankingPlugin.cs Match/MatchEvents.cs
git commit -m "feat: wire SupabaseDatabaseService into plugin load/unload, surface save failures in chat"
```

---

### Task 10: Update `docs/integration-contract.md`

**Files:**
- Modify: `docs/integration-contract.md`

- [ ] **Step 1: Add a note at the top of the document**

Insert this note right after the existing `> [!NOTE]` block at the top of the file (after line 13, before `## 1. Key Integration Rules`):

```markdown
> [!IMPORTANT]
> **Armazenamento ativo:** a partir desta versão, o plugin não usa mais SQLite local —
> os dados descritos na seção 2 (schema) vivem num projeto Supabase (Postgres) dedicado,
> acessado via PostgREST. O schema Postgres e o contrato de funções RPC estão em
> `docs/superpowers/specs/2026-08-02-supabase-persistence-migration-design.md`. A seção 2
> abaixo permanece útil como referência do MODELO de dados (nomes de tabela/coluna são os
> mesmos), mas o SGBD e o mecanismo de acesso mudaram.
```

- [ ] **Step 2: Commit**

```bash
git add docs/integration-contract.md
git commit -m "docs: note that persistence moved from local SQLite to a dedicated Supabase project"
```

---

## Self-Review Notes (for whoever executes this plan)

- **Spec coverage:** Tasks 1–4 cover "Arquitetura" + "Componentes" (interface, fake, DI rewiring, SQLite removal). Tasks 5–8 cover "Schema Postgres" consumption + "Mapeamento dos 23 métodos" + "Funções RPC" (every method in the spec's mapping table has a corresponding implementation here). Task 9 covers "Tratamento de erros" (retry in `WriteMatchEndResultAsync`, non-fatal boot ping, chat-visible save failure) and the config/plugin wiring. Task 10 covers the `docs/integration-contract.md` update the spec calls for. The spec's "Segurança" section (key handling) has no dedicated task because it's a property of how `SendAsync` already builds requests (headers only, never logged) — verify this holds when reviewing Task 5.
- **Not covered by this plan (explicitly out of scope per the spec):** applying the schema/RPC functions to the actual Supabase project, and writing the `plpgsql` bodies — that's the spec's "Fora de escopo," owned by whoever administers Supabase.
