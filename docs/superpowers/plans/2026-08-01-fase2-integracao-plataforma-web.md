# Fase 2 — Integração com a Plataforma Web (rating → nível) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fazer o plugin enviar, de forma assíncrona e resiliente, o rating e o resumo de desempenho de cada jogador para uma API HTTP da plataforma web, substituindo a atribuição manual de nível — implementando o item "Construir a integração real com a plataforma web (rating → nível)" da Fase 2 do `TODO.md`.

**Architecture:** Sync outbound leve por dirty-tracking + push em lote via timer em background (não um outbox clássico de eventos). Toda alteração em `players.rating` (fim de partida ou ação admin) marca o jogador como pendente numa nova tabela `web_sync_queue` (upsert por steamid — estado atual, não log). Um timer do CounterStrikeSharp drena os pendentes periodicamente e envia um `POST` HTTPS assíncrono em lote, com API key simples. `!rating_wipe` é um caso à parte: como apaga jogadores em vez de alterá-los, usa uma flag `wipe_pending` numa tabela de estado singleton e envia um sinal `{"wipe": true}` isolado. Ver design completo em `docs/superpowers/specs/2026-08-01-fase2-integracao-plataforma-web-design.md`.

**Tech Stack:** C# / .NET 8, Microsoft.Data.Sqlite, System.Net.Http (BCL, sem pacote novo), System.Text.Json, CounterStrikeSharp.API, xUnit.

## Global Constraints

- .NET 8, `Nullable` e `ImplicitUsings` habilitados (ver `MixRanking.csproj`).
- Comentários mínimos — só quando o "porquê" não é óbvio, seguindo o padrão já existente no repositório.
- Testes em `MixRanking.Tests` usando xUnit (`[Fact]`), seguindo o padrão de `DatabaseTests.cs`/`RatingTests.cs`: `SqliteConnection` em memória com `Mode=Memory;Cache=Shared`, uma conexão "keep-alive" no construtor para manter o DB vivo entre aberturas do `DatabaseService`.
- `DatabaseService` abre uma nova `SqliteConnection` por método — padrão já existente, não mexer nisso agora (débito técnico separado, listado no `TODO.md`).
- Migration 5 (`web_sync_queue`, `web_sync_state`) — `CurrentSchemaVersion`/`PRAGMA user_version` sobe de 4 para 5. Essas duas tabelas são detalhe interno, **não** fazem parte do contrato externo em `docs/integration-contract.md`.
- `HttpWebSyncClient` (implementação HTTP real) não é coberto por teste automatizado — só a lógica pura (`WebSyncService`, `DatabaseService`) é testada, usando um `IWebSyncClient` fake. Consistente com o design.
- `WebSyncEnabled` tem default `false` — sync nunca fica ativo sem configuração explícita; não deve quebrar quem não configurar.
- Rodar `dotnet test MixRanking.Tests/MixRanking.Tests.csproj` após cada tarefa com teste automatizado, antes de commitar.
- Rodar `dotnet build MixRanking.csproj -c Release` nas tarefas que só tocam código de plugin/infra sem teste automatizado (AdminCommands, MixRankingPlugin, HttpWebSyncClient), antes de commitar.

---

### Task 1: Config fields e modelos de payload

**Files:**
- Modify: `Config/RankingConfig.cs:44-47`
- Create: `Models/WebSyncModels.cs`

**Interfaces:**
- Produces: `RankingConfig.WebSyncEnabled : bool`, `RankingConfig.WebSyncUrl : string`, `RankingConfig.WebSyncApiKey : string`, `RankingConfig.WebSyncIntervalSeconds : int`
- Produces: `WebSyncPlayerPayload`, `WebSyncBatchPayload`, `WebSyncWipePayload` (classes em `MixRanking.Models`)

Sem ciclo de teste — são classes de dados sem lógica. Padrão do resto do plano retoma TDD a partir da Task 2.

- [ ] **Step 1: Adicionar os campos de config**

Em `Config/RankingConfig.cs`, adicionar antes do fechamento da classe (depois de `ChatPrefix`):

```csharp
    /// <summary>Habilita o sync outbound de rating/nível com a plataforma web.</summary>
    [JsonPropertyName("WebSyncEnabled")]
    public bool WebSyncEnabled { get; set; } = false;

    /// <summary>URL HTTPS de destino do sync outbound.</summary>
    [JsonPropertyName("WebSyncUrl")]
    public string WebSyncUrl { get; set; } = "";

    /// <summary>Chave de API enviada no header Authorization do sync outbound.</summary>
    [JsonPropertyName("WebSyncApiKey")]
    public string WebSyncApiKey { get; set; } = "";

    /// <summary>Intervalo em segundos entre drenagens da fila de sync outbound.</summary>
    [JsonPropertyName("WebSyncIntervalSeconds")]
    public int WebSyncIntervalSeconds { get; set; } = 30;
```

- [ ] **Step 2: Criar os modelos de payload**

Criar `Models/WebSyncModels.cs`:

```csharp
using System.Text.Json.Serialization;

namespace MixRanking.Models;

/// <summary>Payload de um jogador no sync outbound para a plataforma web.</summary>
public class WebSyncPlayerPayload
{
    [JsonPropertyName("steamid")]
    public required string SteamId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("rating")]
    public int Rating { get; set; }

    [JsonPropertyName("matches")]
    public int Matches { get; set; }

    [JsonPropertyName("wins")]
    public int Wins { get; set; }

    [JsonPropertyName("losses")]
    public int Losses { get; set; }

    [JsonPropertyName("kills")]
    public int Kills { get; set; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; set; }

    [JsonPropertyName("assists")]
    public int Assists { get; set; }

    [JsonPropertyName("damage")]
    public long Damage { get; set; }

    [JsonPropertyName("mvps")]
    public int Mvps { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>Lote de jogadores enviado no sync outbound.</summary>
public class WebSyncBatchPayload
{
    [JsonPropertyName("players")]
    public required List<WebSyncPlayerPayload> Players { get; set; }
}

/// <summary>Sinal isolado enviado após um !rating_wipe.</summary>
public class WebSyncWipePayload
{
    [JsonPropertyName("wipe")]
    public bool Wipe { get; set; } = true;
}
```

- [ ] **Step 3: Verificar que compila**

Run: `dotnet build MixRanking.csproj -c Release`
Expected: Build succeeded, sem erros.

- [ ] **Step 4: Commit**

```bash
git add Config/RankingConfig.cs Models/WebSyncModels.cs
git commit -m "feat: add web sync config fields and payload models"
```

---

### Task 2: Migration 5 — tabelas `web_sync_queue` e `web_sync_state`

**Files:**
- Modify: `Database/DatabaseService.cs:17` (bump `CurrentSchemaVersion`)
- Modify: `Database/DatabaseService.cs:128-142` (novo const após `Migration4_AdminAuditLog`)
- Modify: `Database/DatabaseService.cs:158` (registrar migration em `InitializeAsync`)
- Test: Modify `MixRanking.Tests/DatabaseTests.cs:29-47`

**Interfaces:**
- Produces: tabela `web_sync_queue` (steamid PK, campos do payload), tabela `web_sync_state` (singleton, `wipe_pending`)

- [ ] **Step 1: Atualizar o teste existente para esperar a versão 5 e as novas tabelas**

Em `MixRanking.Tests/DatabaseTests.cs`, substituir o método `InitializeAsync_RunsMigrationsAndSetsUserVersion` (linhas 29-47):

```csharp
    [Fact]
    public async Task InitializeAsync_RunsMigrationsAndSetsUserVersion()
    {
        // Act
        await _db.InitializeAsync();

        // Assert
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(5, version);

        // Verify seasons exists and contains Season 1
        command.CommandText = "SELECT COUNT(*) FROM seasons WHERE name = 'Season 1' AND is_active = 1;";
        var seasonCount = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, seasonCount);

        // Verify web_sync_queue table exists and starts empty
        command.CommandText = "SELECT COUNT(*) FROM web_sync_queue;";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));

        // Verify web_sync_state singleton row starts with wipe_pending = 0
        command.CommandText = "SELECT wipe_pending FROM web_sync_state WHERE id = 1;";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }
```

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~DatabaseTests.InitializeAsync_RunsMigrationsAndSetsUserVersion"`
Expected: FAIL — `Assert.Equal(5, version)` falha (versão ainda é 4) ou `SqliteException` (tabela `web_sync_queue` não existe).

- [ ] **Step 3: Implementar a Migration 5**

Em `Database/DatabaseService.cs:17`, mudar:

```csharp
    private const int CurrentSchemaVersion = 4;
```

para:

```csharp
    private const int CurrentSchemaVersion = 5;
```

Depois do bloco `Migration4_AdminAuditLog` (linhas 128-142), adicionar:

```csharp

    private const string Migration5_WebSyncQueue = @"
        CREATE TABLE IF NOT EXISTS web_sync_queue (
            steamid TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            rating INTEGER NOT NULL,
            matches INTEGER NOT NULL,
            wins INTEGER NOT NULL,
            losses INTEGER NOT NULL,
            kills INTEGER NOT NULL,
            deaths INTEGER NOT NULL,
            assists INTEGER NOT NULL,
            damage INTEGER NOT NULL,
            mvps INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS web_sync_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            wipe_pending INTEGER NOT NULL DEFAULT 0
        );
        INSERT INTO web_sync_state (id, wipe_pending) VALUES (1, 0);
    ";
```

Em `InitializeAsync` (linha 158, logo após o `if (version < 4) { ... }`), adicionar:

```csharp
        if (version < 5) { await RunMigrationAsync(connection, Migration5_WebSyncQueue); version = 5; await SetUserVersionAsync(connection, version); }
```

- [ ] **Step 4: Rodar o teste e confirmar que passa**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~DatabaseTests.InitializeAsync_RunsMigrationsAndSetsUserVersion"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Database/DatabaseService.cs MixRanking.Tests/DatabaseTests.cs
git commit -m "feat: add web_sync_queue and web_sync_state migration"
```

---

### Task 3: `UpsertWebSyncQueueAsync` e `GetPendingWebSyncEntriesAsync`

**Files:**
- Modify: `Database/DatabaseService.cs` (novos métodos após `GetActiveSeasonIdAsync`, ~linha 456)
- Test: Modify `MixRanking.Tests/DatabaseTests.cs` (novos métodos após `ResetAllDataWithAuditAsync_ClearsStatsButRetainsAuditLog`, ~linha 314)

**Interfaces:**
- Produces: `DatabaseService.UpsertWebSyncQueueAsync(PlayerData player) : Task`
- Produces: `DatabaseService.GetPendingWebSyncEntriesAsync(int limit) : Task<List<PlayerData>>`

- [ ] **Step 1: Escrever os testes que falham**

Adicionar em `MixRanking.Tests/DatabaseTests.cs`, antes do fechamento da classe:

```csharp
    [Fact]
    public async Task UpsertWebSyncQueueAsync_InsertsAndOverwritesBySteamId()
    {
        await _db.InitializeAsync();

        var first = new PlayerData
        {
            SteamId = "76561198000000010",
            Name = "SyncPlayer",
            Rating = 1000,
            Matches = 1,
            Wins = 1,
            Losses = 0,
            Kills = 10,
            Deaths = 5,
            Assists = 2,
            Damage = 1500,
            Mvps = 1,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        // Act: insert
        await _db.UpsertWebSyncQueueAsync(first);
        var afterFirstInsert = await _db.GetPendingWebSyncEntriesAsync(limit: 10);

        // Assert: one entry with the inserted values
        Assert.Single(afterFirstInsert);
        Assert.Equal(1000, afterFirstInsert[0].Rating);
        Assert.Equal(1, afterFirstInsert[0].Matches);

        // Act: upsert same steamid with updated values
        var updated = new PlayerData
        {
            SteamId = "76561198000000010",
            Name = "SyncPlayer",
            Rating = 1050,
            Matches = 2,
            Wins = 2,
            Losses = 0,
            Kills = 20,
            Deaths = 8,
            Assists = 4,
            Damage = 3000,
            Mvps = 2,
            CreatedAt = first.CreatedAt
        };
        await _db.UpsertWebSyncQueueAsync(updated);
        var afterUpsert = await _db.GetPendingWebSyncEntriesAsync(limit: 10);

        // Assert: still only one entry, with the new values (no duplicate row)
        Assert.Single(afterUpsert);
        Assert.Equal(1050, afterUpsert[0].Rating);
        Assert.Equal(2, afterUpsert[0].Matches);
    }

    [Fact]
    public async Task GetPendingWebSyncEntriesAsync_RespectsLimit()
    {
        await _db.InitializeAsync();

        for (int i = 0; i < 5; i++)
        {
            await _db.UpsertWebSyncQueueAsync(new PlayerData
            {
                SteamId = $"7656119800000{i:D4}",
                Name = $"Player{i}",
                Rating = 1000 + i,
                CreatedAt = DateTime.UtcNow
            });
        }

        var limited = await _db.GetPendingWebSyncEntriesAsync(limit: 3);

        Assert.Equal(3, limited.Count);
    }
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~DatabaseTests.UpsertWebSyncQueueAsync_InsertsAndOverwritesBySteamId|FullyQualifiedName~DatabaseTests.GetPendingWebSyncEntriesAsync_RespectsLimit"`
Expected: FAIL — `DatabaseService` não contém `UpsertWebSyncQueueAsync`/`GetPendingWebSyncEntriesAsync` (erro de compilação).

- [ ] **Step 3: Implementar os métodos**

Em `Database/DatabaseService.cs`, adicionar após `GetActiveSeasonIdAsync` (antes de `WriteMatchEndResultAsync`):

```csharp
    /// <summary>Marca (upsert) o estado atual de um jogador como pendente de sync com a plataforma web.</summary>
    public async Task UpsertWebSyncQueueAsync(PlayerData player)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO web_sync_queue (steamid, name, rating, matches, wins, losses, kills, deaths, assists, damage, mvps, created_at)
            VALUES ($steamId, $name, $rating, $matches, $wins, $losses, $kills, $deaths, $assists, $damage, $mvps, $createdAt)
            ON CONFLICT(steamid) DO UPDATE SET
                name = $name, rating = $rating, matches = $matches, wins = $wins, losses = $losses,
                kills = $kills, deaths = $deaths, assists = $assists, damage = $damage, mvps = $mvps,
                created_at = $createdAt";
        command.Parameters.AddWithValue("$steamId", player.SteamId);
        command.Parameters.AddWithValue("$name", player.Name);
        command.Parameters.AddWithValue("$rating", player.Rating);
        command.Parameters.AddWithValue("$matches", player.Matches);
        command.Parameters.AddWithValue("$wins", player.Wins);
        command.Parameters.AddWithValue("$losses", player.Losses);
        command.Parameters.AddWithValue("$kills", player.Kills);
        command.Parameters.AddWithValue("$deaths", player.Deaths);
        command.Parameters.AddWithValue("$assists", player.Assists);
        command.Parameters.AddWithValue("$damage", player.Damage);
        command.Parameters.AddWithValue("$mvps", player.Mvps);
        command.Parameters.AddWithValue("$createdAt", player.CreatedAt.ToString("o"));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Retorna até <paramref name="limit"/> jogadores pendentes de sync com a plataforma web.</summary>
    public async Task<List<PlayerData>> GetPendingWebSyncEntriesAsync(int limit)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT steamid, name, rating, matches, wins, losses, kills, deaths, assists, damage, mvps, created_at
            FROM web_sync_queue
            LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<PlayerData>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new PlayerData
            {
                SteamId = reader.GetString(0),
                Name = reader.GetString(1),
                Rating = reader.GetInt32(2),
                Matches = reader.GetInt32(3),
                Wins = reader.GetInt32(4),
                Losses = reader.GetInt32(5),
                Kills = reader.GetInt32(6),
                Deaths = reader.GetInt32(7),
                Assists = reader.GetInt32(8),
                Damage = reader.GetInt64(9),
                Mvps = reader.GetInt32(10),
                CreatedAt = DateTime.Parse(reader.GetString(11))
            });
        }
        return entries;
    }
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~DatabaseTests.UpsertWebSyncQueueAsync_InsertsAndOverwritesBySteamId|FullyQualifiedName~DatabaseTests.GetPendingWebSyncEntriesAsync_RespectsLimit"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Database/DatabaseService.cs MixRanking.Tests/DatabaseTests.cs
git commit -m "feat: add web sync queue upsert and read methods"
```

---

### Task 4: `ClearWebSyncQueueEntriesAsync`

**Files:**
- Modify: `Database/DatabaseService.cs` (novo método após `GetPendingWebSyncEntriesAsync`)
- Test: Modify `MixRanking.Tests/DatabaseTests.cs`

**Interfaces:**
- Consumes: `DatabaseService.UpsertWebSyncQueueAsync`, `DatabaseService.GetPendingWebSyncEntriesAsync` (Task 3)
- Produces: `DatabaseService.ClearWebSyncQueueEntriesAsync(List<string> steamIds) : Task`

- [ ] **Step 1: Escrever o teste que falha**

```csharp
    [Fact]
    public async Task ClearWebSyncQueueEntriesAsync_RemovesOnlySpecifiedEntries()
    {
        await _db.InitializeAsync();

        await _db.UpsertWebSyncQueueAsync(new PlayerData { SteamId = "76561198000000021", Name = "A", CreatedAt = DateTime.UtcNow });
        await _db.UpsertWebSyncQueueAsync(new PlayerData { SteamId = "76561198000000022", Name = "B", CreatedAt = DateTime.UtcNow });

        // Act
        await _db.ClearWebSyncQueueEntriesAsync(new List<string> { "76561198000000021" });

        // Assert
        var remaining = await _db.GetPendingWebSyncEntriesAsync(limit: 10);
        Assert.Single(remaining);
        Assert.Equal("76561198000000022", remaining[0].SteamId);
    }
```

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~DatabaseTests.ClearWebSyncQueueEntriesAsync_RemovesOnlySpecifiedEntries"`
Expected: FAIL — método não existe (erro de compilação).

- [ ] **Step 3: Implementar o método**

Em `Database/DatabaseService.cs`, adicionar após `GetPendingWebSyncEntriesAsync`:

```csharp
    /// <summary>Remove da fila os jogadores já sincronizados com sucesso.</summary>
    public async Task ClearWebSyncQueueEntriesAsync(List<string> steamIds)
    {
        if (steamIds.Count == 0) return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        var parameterNames = new List<string>();
        for (int i = 0; i < steamIds.Count; i++)
        {
            var paramName = $"$steamId{i}";
            parameterNames.Add(paramName);
            command.Parameters.AddWithValue(paramName, steamIds[i]);
        }
        command.CommandText = $"DELETE FROM web_sync_queue WHERE steamid IN ({string.Join(",", parameterNames)})";
        await command.ExecuteNonQueryAsync();
    }
```

- [ ] **Step 4: Rodar o teste e confirmar que passa**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~DatabaseTests.ClearWebSyncQueueEntriesAsync_RemovesOnlySpecifiedEntries"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Database/DatabaseService.cs MixRanking.Tests/DatabaseTests.cs
git commit -m "feat: add web sync queue clear method"
```

---

### Task 5: Estado de `wipe_pending`

**Files:**
- Modify: `Database/DatabaseService.cs` (novos métodos após `ClearWebSyncQueueEntriesAsync`)
- Test: Modify `MixRanking.Tests/DatabaseTests.cs`

**Interfaces:**
- Produces: `DatabaseService.IsWipePendingAsync() : Task<bool>`, `DatabaseService.SetWipePendingAsync() : Task`, `DatabaseService.ClearWipePendingAsync() : Task`

- [ ] **Step 1: Escrever o teste que falha**

```csharp
    [Fact]
    public async Task WipePendingState_DefaultsFalseThenTogglesCorrectly()
    {
        await _db.InitializeAsync();

        Assert.False(await _db.IsWipePendingAsync());

        await _db.SetWipePendingAsync();
        Assert.True(await _db.IsWipePendingAsync());

        await _db.ClearWipePendingAsync();
        Assert.False(await _db.IsWipePendingAsync());
    }
```

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~DatabaseTests.WipePendingState_DefaultsFalseThenTogglesCorrectly"`
Expected: FAIL — métodos não existem (erro de compilação).

- [ ] **Step 3: Implementar os métodos**

Em `Database/DatabaseService.cs`, adicionar após `ClearWebSyncQueueEntriesAsync`:

```csharp
    /// <summary>Indica se um !rating_wipe está pendente de sinalização à plataforma web.</summary>
    public async Task<bool> IsWipePendingAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT wipe_pending FROM web_sync_state WHERE id = 1";
        var result = await command.ExecuteScalarAsync();
        return result != null && Convert.ToInt32(result) == 1;
    }

    /// <summary>Marca que um wipe precisa ser sinalizado à plataforma web no próximo ciclo de sync.</summary>
    public async Task SetWipePendingAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE web_sync_state SET wipe_pending = 1 WHERE id = 1";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Limpa a sinalização de wipe pendente após envio bem-sucedido.</summary>
    public async Task ClearWipePendingAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE web_sync_state SET wipe_pending = 0 WHERE id = 1";
        await command.ExecuteNonQueryAsync();
    }
```

- [ ] **Step 4: Rodar o teste e confirmar que passa**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~DatabaseTests.WipePendingState_DefaultsFalseThenTogglesCorrectly"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Database/DatabaseService.cs MixRanking.Tests/DatabaseTests.cs
git commit -m "feat: add wipe-pending state tracking"
```

---

### Task 6: Ligar `WIPE` ao sync (limpar fila + sinalizar wipe_pending)

**Files:**
- Modify: `Database/DatabaseService.cs:825-834` (`ResetAllDataWithAuditAsync`)
- Test: Modify `MixRanking.Tests/DatabaseTests.cs:289-314`

**Interfaces:**
- Consumes: `DatabaseService.UpsertWebSyncQueueAsync`, `GetPendingWebSyncEntriesAsync`, `IsWipePendingAsync` (Tasks 3, 5)

- [ ] **Step 1: Atualizar o teste existente para verificar o efeito no sync**

Substituir `ResetAllDataWithAuditAsync_ClearsStatsButRetainsAuditLog` em `MixRanking.Tests/DatabaseTests.cs`:

```csharp
    [Fact]
    public async Task ResetAllDataWithAuditAsync_ClearsStatsButRetainsAuditLog()
    {
        await _db.InitializeAsync();
        await _db.GetOrCreatePlayerAsync("76561198000000003", "WipeTarget", 1000);
        await _db.UpsertWebSyncQueueAsync(new PlayerData { SteamId = "76561198000000003", Name = "WipeTarget", Rating = 1000, CreatedAt = DateTime.UtcNow });

        // Act
        await _db.ResetAllDataWithAuditAsync("76561198000000000", "OwnerConsole", "Hard reset");

        // Assert
        var player = await _db.GetPlayerAsync("76561198000000003");
        Assert.Null(player);

        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM players;";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));

        command.CommandText = "SELECT action, admin_name, reason FROM admin_audit_log WHERE action = 'WIPE';";
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("WIPE", reader.GetString(0));
        Assert.Equal("OwnerConsole", reader.GetString(1));
        Assert.Equal("Hard reset", reader.GetString(2));

        // Web sync: fila limpa e wipe sinalizado para o próximo ciclo de drenagem
        Assert.Empty(await _db.GetPendingWebSyncEntriesAsync(limit: 10));
        Assert.True(await _db.IsWipePendingAsync());
    }
```

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~DatabaseTests.ResetAllDataWithAuditAsync_ClearsStatsButRetainsAuditLog"`
Expected: FAIL — `web_sync_queue` ainda contém a entrada e `wipe_pending` continua `0`.

- [ ] **Step 3: Atualizar `ResetAllDataWithAuditAsync`**

Em `Database/DatabaseService.cs:825-834`, mudar:

```csharp
                var deleteCmd = connection.CreateCommand();
                deleteCmd.Transaction = (SqliteTransaction)transaction;
                deleteCmd.CommandText = @"
                    DELETE FROM rating_history;
                    DELETE FROM match_player_stats;
                    DELETE FROM season_ratings;
                    DELETE FROM matches;
                    DELETE FROM players;
                ";
                await deleteCmd.ExecuteNonQueryAsync();
```

para:

```csharp
                var deleteCmd = connection.CreateCommand();
                deleteCmd.Transaction = (SqliteTransaction)transaction;
                deleteCmd.CommandText = @"
                    DELETE FROM rating_history;
                    DELETE FROM match_player_stats;
                    DELETE FROM season_ratings;
                    DELETE FROM matches;
                    DELETE FROM players;
                    DELETE FROM web_sync_queue;
                    UPDATE web_sync_state SET wipe_pending = 1 WHERE id = 1;
                ";
                await deleteCmd.ExecuteNonQueryAsync();
```

- [ ] **Step 4: Rodar o teste e confirmar que passa**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~DatabaseTests.ResetAllDataWithAuditAsync_ClearsStatsButRetainsAuditLog"`
Expected: PASS

- [ ] **Step 5: Rodar a suíte completa de `DatabaseTests`**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~DatabaseTests"`
Expected: PASS (todos os testes de `DatabaseTests`)

- [ ] **Step 6: Commit**

```bash
git add Database/DatabaseService.cs MixRanking.Tests/DatabaseTests.cs
git commit -m "feat: wire rating wipe into web sync queue and pending flag"
```

---

### Task 7: `IWebSyncClient` e `WebSyncService`

**Files:**
- Create: `Services/IWebSyncClient.cs`
- Create: `Services/WebSyncService.cs`
- Test: Create `MixRanking.Tests/WebSyncServiceTests.cs`

**Interfaces:**
- Consumes: `DatabaseService.UpsertWebSyncQueueAsync`, `GetPendingWebSyncEntriesAsync`, `ClearWebSyncQueueEntriesAsync`, `IsWipePendingAsync`, `SetWipePendingAsync`, `ClearWipePendingAsync` (Tasks 3, 4, 5)
- Produces: `IWebSyncClient.SendPlayersAsync(IReadOnlyList<PlayerData> players) : Task<bool>`, `IWebSyncClient.SendWipeAsync() : Task<bool>`
- Produces: `WebSyncService(DatabaseService db, IWebSyncClient client, ILogger logger)`, `WebSyncService.MarkDirtyAsync(PlayerData player) : Task`, `WebSyncService.DrainPendingAsync() : Task`
- Produces (em `MixRanking.Tests`): `FakeWebSyncClient : IWebSyncClient` — usada também nas Tasks 9 e nas atualizações de `RatingTests.cs`.

- [ ] **Step 1: Escrever a interface**

Criar `Services/IWebSyncClient.cs`:

```csharp
using MixRanking.Models;

namespace MixRanking.Services;

/// <summary>Cliente do sync outbound de rating/nível com a plataforma web.</summary>
public interface IWebSyncClient
{
    /// <summary>Envia um lote de jogadores. Retorna true se o lote inteiro foi aceito (200).</summary>
    Task<bool> SendPlayersAsync(IReadOnlyList<PlayerData> players);

    /// <summary>Envia o sinal de wipe. Retorna true se aceito (200).</summary>
    Task<bool> SendWipeAsync();
}
```

- [ ] **Step 2: Escrever os testes que falham**

Criar `MixRanking.Tests/WebSyncServiceTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MixRanking.Database;
using MixRanking.Models;
using MixRanking.Services;
using Xunit;

namespace MixRanking.Tests;

/// <summary>Fake de IWebSyncClient reutilizado por WebSyncServiceTests e RatingTests.</summary>
internal class FakeWebSyncClient : IWebSyncClient
{
    public bool SendPlayersResult { get; set; } = true;
    public bool SendWipeResult { get; set; } = true;
    public List<IReadOnlyList<PlayerData>> SentBatches { get; } = new();
    public int WipeCallCount { get; private set; }
    public int SendPlayersCallCount { get; private set; }

    public Task<bool> SendPlayersAsync(IReadOnlyList<PlayerData> players)
    {
        SendPlayersCallCount++;
        SentBatches.Add(players);
        return Task.FromResult(SendPlayersResult);
    }

    public Task<bool> SendWipeAsync()
    {
        WipeCallCount++;
        return Task.FromResult(SendWipeResult);
    }
}

public class WebSyncServiceTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private const string ConnectionString = "Data Source=InMemoryDbWebSync;Mode=Memory;Cache=Shared";
    private readonly DatabaseService _db;

    public WebSyncServiceTests()
    {
        _keepAliveConnection = new SqliteConnection(ConnectionString);
        _keepAliveConnection.Open();
        _db = new DatabaseService("InMemoryDbWebSync;Mode=Memory;Cache=Shared");
    }

    public void Dispose()
    {
        _keepAliveConnection.Close();
        _keepAliveConnection.Dispose();
    }

    [Fact]
    public async Task MarkDirtyAsync_UpsertsPlayerIntoQueue()
    {
        await _db.InitializeAsync();
        var client = new FakeWebSyncClient();
        var service = new WebSyncService(_db, client, NullLogger.Instance);

        var player = new PlayerData { SteamId = "76561198000000030", Name = "Dirty", Rating = 1200, CreatedAt = DateTime.UtcNow };

        // Act
        await service.MarkDirtyAsync(player);

        // Assert
        var pending = await _db.GetPendingWebSyncEntriesAsync(limit: 10);
        Assert.Single(pending);
        Assert.Equal("76561198000000030", pending[0].SteamId);
        Assert.Equal(1200, pending[0].Rating);
    }

    [Fact]
    public async Task DrainPendingAsync_ClearsQueueOnSuccess()
    {
        await _db.InitializeAsync();
        var client = new FakeWebSyncClient { SendPlayersResult = true };
        var service = new WebSyncService(_db, client, NullLogger.Instance);
        await service.MarkDirtyAsync(new PlayerData { SteamId = "76561198000000031", Name = "P1", CreatedAt = DateTime.UtcNow });

        // Act
        await service.DrainPendingAsync();

        // Assert
        Assert.Empty(await _db.GetPendingWebSyncEntriesAsync(limit: 10));
        Assert.Equal(1, client.SendPlayersCallCount);
    }

    [Fact]
    public async Task DrainPendingAsync_KeepsQueuePendingOnFailure()
    {
        await _db.InitializeAsync();
        var client = new FakeWebSyncClient { SendPlayersResult = false };
        var service = new WebSyncService(_db, client, NullLogger.Instance);
        await service.MarkDirtyAsync(new PlayerData { SteamId = "76561198000000032", Name = "P2", CreatedAt = DateTime.UtcNow });

        // Act
        await service.DrainPendingAsync();

        // Assert
        var pending = await _db.GetPendingWebSyncEntriesAsync(limit: 10);
        Assert.Single(pending);
        Assert.Equal("76561198000000032", pending[0].SteamId);
    }

    [Fact]
    public async Task DrainPendingAsync_SendsWipeSignal_WhenWipePending()
    {
        await _db.InitializeAsync();
        await _db.SetWipePendingAsync();
        var client = new FakeWebSyncClient { SendWipeResult = true };
        var service = new WebSyncService(_db, client, NullLogger.Instance);

        // Act
        await service.DrainPendingAsync();

        // Assert: sinal de wipe enviado, fila normal não tocada nesse ciclo, flag limpa
        Assert.Equal(1, client.WipeCallCount);
        Assert.Equal(0, client.SendPlayersCallCount);
        Assert.False(await _db.IsWipePendingAsync());
    }
}
```

- [ ] **Step 3: Rodar os testes e confirmar que falham**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~WebSyncServiceTests"`
Expected: FAIL — `WebSyncService` não existe (erro de compilação).

- [ ] **Step 4: Implementar `WebSyncService`**

Criar `Services/WebSyncService.cs`:

```csharp
using Microsoft.Extensions.Logging;
using MixRanking.Database;
using MixRanking.Models;

namespace MixRanking.Services;

/// <summary>
/// Orquestra o sync outbound de rating/nível com a plataforma web: dirty-tracking local
/// e drenagem em lote via HTTP, sem bloquear o fluxo principal do plugin.
/// </summary>
public class WebSyncService
{
    private const int BatchLimit = 100;

    private readonly DatabaseService _db;
    private readonly IWebSyncClient _client;
    private readonly ILogger _logger;

    public WebSyncService(DatabaseService db, IWebSyncClient client, ILogger logger)
    {
        _db = db;
        _client = client;
        _logger = logger;
    }

    /// <summary>Marca o estado atual de um jogador como pendente de sync. Nunca lança exceção.</summary>
    public async Task MarkDirtyAsync(PlayerData player)
    {
        try
        {
            await _db.UpsertWebSyncQueueAsync(player);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MixRanking] Failed to mark player {SteamId} for web sync.", player.SteamId);
        }
    }

    /// <summary>Drena a fila pendente (ou sinaliza wipe) para a plataforma web. Nunca lança exceção.</summary>
    public async Task DrainPendingAsync()
    {
        try
        {
            if (await _db.IsWipePendingAsync())
            {
                bool wipeSent = await _client.SendWipeAsync();
                if (wipeSent)
                {
                    await _db.ClearWipePendingAsync();
                }
                else
                {
                    _logger.LogWarning("[MixRanking] Web sync wipe signal failed, will retry next tick.");
                }
                return;
            }

            var pending = await _db.GetPendingWebSyncEntriesAsync(BatchLimit);
            if (pending.Count == 0) return;

            bool success = await _client.SendPlayersAsync(pending);
            if (success)
            {
                await _db.ClearWebSyncQueueEntriesAsync(pending.Select(p => p.SteamId).ToList());
            }
            else
            {
                _logger.LogWarning("[MixRanking] Web sync failed, {Count} player(s) remain pending.", pending.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MixRanking] Unexpected error during web sync drain.");
        }
    }
}
```

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~WebSyncServiceTests"`
Expected: PASS (4 testes)

- [ ] **Step 6: Commit**

```bash
git add Services/IWebSyncClient.cs Services/WebSyncService.cs MixRanking.Tests/WebSyncServiceTests.cs
git commit -m "feat: add WebSyncService with dirty-tracking and batch drain"
```

---

### Task 8: `HttpWebSyncClient` (implementação HTTP real)

**Files:**
- Create: `Services/HttpWebSyncClient.cs`

**Interfaces:**
- Consumes: `IWebSyncClient` (Task 7), `RankingConfig.WebSyncUrl`/`WebSyncApiKey`, `WebSyncBatchPayload`/`WebSyncPlayerPayload`/`WebSyncWipePayload` (Task 1)
- Produces: `HttpWebSyncClient(RankingConfig config)`, implementa `IWebSyncClient`, `IDisposable`

Sem teste automatizado — chamada HTTP real, fora do escopo de teste unitário por decisão do design (`docs/superpowers/specs/2026-08-01-fase2-integracao-plataforma-web-design.md`, seção Testes).

- [ ] **Step 1: Implementar `HttpWebSyncClient`**

Criar `Services/HttpWebSyncClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MixRanking.Config;
using MixRanking.Models;

namespace MixRanking.Services;

/// <summary>Cliente HTTP real do sync outbound para a plataforma web. Conexão sempre de saída.</summary>
public class HttpWebSyncClient : IWebSyncClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly RankingConfig _config;

    public HttpWebSyncClient(RankingConfig config)
    {
        _config = config;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public Task<bool> SendPlayersAsync(IReadOnlyList<PlayerData> players)
    {
        var payload = new WebSyncBatchPayload
        {
            Players = players.Select(p => new WebSyncPlayerPayload
            {
                SteamId = p.SteamId,
                Name = p.Name,
                Rating = p.Rating,
                Matches = p.Matches,
                Wins = p.Wins,
                Losses = p.Losses,
                Kills = p.Kills,
                Deaths = p.Deaths,
                Assists = p.Assists,
                Damage = p.Damage,
                Mvps = p.Mvps,
                CreatedAt = p.CreatedAt
            }).ToList()
        };

        return PostAsync(payload);
    }

    public Task<bool> SendWipeAsync()
    {
        return PostAsync(new WebSyncWipePayload());
    }

    private async Task<bool> PostAsync<T>(T payload)
    {
        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, _config.WebSyncUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.WebSyncApiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
```

- [ ] **Step 2: Verificar que compila**

Run: `dotnet build MixRanking.csproj -c Release`
Expected: Build succeeded, sem erros.

- [ ] **Step 3: Commit**

```bash
git add Services/HttpWebSyncClient.cs
git commit -m "feat: add HTTP implementation of web sync client"
```

---

### Task 9: Marcar jogadores como pendentes ao fim de partida (`RatingService`)

**Files:**
- Modify: `Services/RatingService.cs:13-22` (constructor), `Services/RatingService.cs:149-151` (após `WriteMatchEndResultAsync`)
- Test: Modify `MixRanking.Tests/RatingTests.cs:11-37` (constructor), adicionar novo teste

**Interfaces:**
- Consumes: `WebSyncService.MarkDirtyAsync` (Task 7), `DatabaseService.GetPlayersBySteamIdsAsync` (já existente), `FakeWebSyncClient` (Task 7, definida em `MixRanking.Tests`)
- Produces: `RatingService(DatabaseService db, RankingConfig config, WebSyncService webSyncService)` — assinatura do construtor muda

- [ ] **Step 1: Atualizar `RatingTests.cs` para a nova assinatura e escrever o teste que falha**

Em `MixRanking.Tests/RatingTests.cs`, adicionar o campo e ajustar o construtor (linhas 11-37):

```csharp
public class RatingTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private const string ConnectionString = "Data Source=InMemoryDbRating;Mode=Memory;Cache=Shared";
    private readonly DatabaseService _db;
    private readonly RankingConfig _config;
    private readonly WebSyncService _webSyncService;
    private readonly RatingService _ratingService;

    public RatingTests()
    {
        _keepAliveConnection = new SqliteConnection(ConnectionString);
        _keepAliveConnection.Open();
        _db = new DatabaseService("InMemoryDbRating;Mode=Memory;Cache=Shared");

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
        _ratingService = new RatingService(_db, _config, _webSyncService);
    }
```

Adicionar os `using` necessários no topo do arquivo (se ainda não presentes):

```csharp
using Microsoft.Extensions.Logging.Abstractions;
```

Adicionar o novo teste, antes do fechamento da classe:

```csharp
    [Fact]
    public async Task ProcessMatchEndAsync_MarksUpdatedPlayerForWebSync()
    {
        await _db.InitializeAsync();

        var stats = new MatchPlayerStats
        {
            SteamId = 76561198000000300,
            PlayerName = "SyncedPlayer",
            Team = CsTeam.CounterTerrorist,
            Kills = 20,
            Deaths = 10,
            Assists = 5,
            Damage = 2000,
            RoundsPlayed = 20,
            RoundsSurvived = 10,
            RoundsWithKill = 15,
            RoundsWithKast = 18,
            Mvps = 2,
            OpeningKills = 1,
            OpeningDeaths = 0,
            TradeKills = 0,
            FlashAssists = 1,
            Abandoned = false
        };

        var playerStats = new Dictionary<ulong, MatchPlayerStats> { { stats.SteamId, stats } };

        // Act
        var changes = await _ratingService.ProcessMatchEndAsync(
            Guid.NewGuid().ToString(), "de_mirage", CsTeam.CounterTerrorist, 13, 7, playerStats);

        // Assert: o jogador foi marcado como pendente com os valores acumulados pós-partida
        var pending = await _db.GetPendingWebSyncEntriesAsync(limit: 10);
        Assert.Single(pending);
        var entry = pending[0];
        Assert.Equal("76561198000000300", entry.SteamId);
        Assert.Equal(changes[0].NewRating, entry.Rating);
        Assert.Equal(1, entry.Matches);
        Assert.Equal(1, entry.Wins);
        Assert.Equal(0, entry.Losses);
        Assert.Equal(20, entry.Kills);
        Assert.Equal(10, entry.Deaths);
        Assert.Equal(5, entry.Assists);
        Assert.Equal(2000, entry.Damage);
        Assert.Equal(2, entry.Mvps);
    }
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~RatingTests"`
Expected: FAIL — erro de compilação (`RatingService` não aceita `WebSyncService` no construtor ainda).

- [ ] **Step 3: Atualizar `RatingService`**

Em `Services/RatingService.cs:13-22`, mudar:

```csharp
public class RatingService
{
    private readonly DatabaseService _db;
    private readonly RankingConfig _config;

    public RatingService(DatabaseService db, RankingConfig config)
    {
        _db = db;
        _config = config;
    }
```

para:

```csharp
public class RatingService
{
    private readonly DatabaseService _db;
    private readonly RankingConfig _config;
    private readonly WebSyncService _webSyncService;

    public RatingService(DatabaseService db, RankingConfig config, WebSyncService webSyncService)
    {
        _db = db;
        _config = config;
        _webSyncService = webSyncService;
    }
```

Em `Services/RatingService.cs:149-151`, mudar:

```csharp
        await _db.WriteMatchEndResultAsync(matchRecord, playerUpdates, _config.InitialRating, activeSeasonId);

        return ratingChanges;
```

para:

```csharp
        await _db.WriteMatchEndResultAsync(matchRecord, playerUpdates, _config.InitialRating, activeSeasonId);

        var updatedSteamIds = playerUpdates.Select(u => u.PlayerData.SteamId).ToList();
        var freshPlayers = await _db.GetPlayersBySteamIdsAsync(updatedSteamIds);
        foreach (var freshPlayer in freshPlayers.Values)
        {
            await _webSyncService.MarkDirtyAsync(freshPlayer);
        }

        return ratingChanges;
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj --filter "FullyQualifiedName~RatingTests"`
Expected: PASS (todos os testes de `RatingTests`, incluindo o novo)

- [ ] **Step 5: Rodar a suíte completa de testes**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj`
Expected: PASS (todos os testes do projeto)

- [ ] **Step 6: Commit**

```bash
git add Services/RatingService.cs MixRanking.Tests/RatingTests.cs
git commit -m "feat: mark players dirty for web sync after match end"
```

---

### Task 10: Marcar jogadores como pendentes em ações admin (`AdminCommands`)

**Files:**
- Modify: `Commands/AdminCommands.cs:1-21` (usings, campo, construtor)
- Modify: `Commands/AdminCommands.cs:67-87` (`OnRatingSet`)
- Modify: `Commands/AdminCommands.cs:116-136` (`OnRatingReset`)
- Modify: `Commands/AdminCommands.cs:171-192` (`OnRatingAdd`)
- Modify: `Commands/AdminCommands.cs:227-248` (`OnRatingRemove`)

**Interfaces:**
- Consumes: `WebSyncService.MarkDirtyAsync` (Task 7)
- Produces: `AdminCommands(DatabaseService db, RankingConfig config, WebSyncService webSyncService)` — assinatura do construtor muda

`!rating_wipe` (`OnRatingWipe`) não precisa de mudança: `ResetAllDataWithAuditAsync` já cuida da fila e da flag de wipe internamente (Task 6).

Sem teste automatizado — `CCSPlayerController`/`CommandInfo` exigem runtime do CounterStrikeSharp, e não há testes existentes para `AdminCommands` no projeto (mesmo padrão já estabelecido).

- [ ] **Step 1: Adicionar o using, o campo, o construtor e o helper de sync**

Em `Commands/AdminCommands.cs:1-21`, adicionar o using, o campo e um helper privado reutilizado pelos quatro comandos que alteram rating (evita repetir "buscar jogador atualizado + marcar dirty" em cada handler):

```csharp
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using MixRanking.Config;
using MixRanking.Database;
using MixRanking.Services;

namespace MixRanking.Commands;

/// <summary>Comandos administrativos para gerenciar ratings.</summary>
public class AdminCommands
{
    private readonly DatabaseService _db;
    private readonly RankingConfig _config;
    private readonly WebSyncService _webSyncService;

    public AdminCommands(DatabaseService db, RankingConfig config, WebSyncService webSyncService)
    {
        _db = db;
        _config = config;
        _webSyncService = webSyncService;
    }

    /// <summary>Busca o estado atual do jogador após uma alteração de rating e marca para sync com a plataforma web.</summary>
    private async Task MarkPlayerDirtyForWebSyncAsync(string steamId)
    {
        var updatedPlayer = await _db.GetPlayerAsync(steamId);
        if (updatedPlayer != null)
        {
            await _webSyncService.MarkDirtyAsync(updatedPlayer);
        }
    }
```

- [ ] **Step 2: Marcar dirty após `OnRatingSet`**

Em `Commands/AdminCommands.cs:78-79`, mudar:

```csharp
                await _db.SetPlayerRatingWithAuditAsync(targetSteamId, newRating, adminSteamId, adminName, reason);
                PrintToPlayer(slot, $"{ChatColors.Green}Rating de {targetPlayer.Name} setado para {newRating}.");
```

para:

```csharp
                await _db.SetPlayerRatingWithAuditAsync(targetSteamId, newRating, adminSteamId, adminName, reason);
                await MarkPlayerDirtyForWebSyncAsync(targetSteamId);
                PrintToPlayer(slot, $"{ChatColors.Green}Rating de {targetPlayer.Name} setado para {newRating}.");
```

- [ ] **Step 3: Marcar dirty após `OnRatingReset`**

Em `Commands/AdminCommands.cs:127-128`, mudar:

```csharp
                await _db.ResetPlayerWithAuditAsync(targetSteamId, _config.InitialRating, adminSteamId, adminName, reason);
                PrintToPlayer(slot, $"{ChatColors.Green}Jogador {targetPlayer.Name} resetado para {_config.InitialRating}.");
```

para:

```csharp
                await _db.ResetPlayerWithAuditAsync(targetSteamId, _config.InitialRating, adminSteamId, adminName, reason);
                await MarkPlayerDirtyForWebSyncAsync(targetSteamId);
                PrintToPlayer(slot, $"{ChatColors.Green}Jogador {targetPlayer.Name} resetado para {_config.InitialRating}.");
```

- [ ] **Step 4: Marcar dirty após `OnRatingAdd`**

Em `Commands/AdminCommands.cs:182-184`, mudar:

```csharp
                await _db.AdjustPlayerRatingWithAuditAsync(targetSteamId, amount, isAdd: true, minRating: _config.MinRating, adminSteamId: adminSteamId, adminName: adminName, reason: reason);
                int newRating = targetPlayer.Rating + amount;
                PrintToPlayer(slot, $"{ChatColors.Green}+{amount} rating para {targetPlayer.Name}. Novo: {newRating}.");
```

para:

```csharp
                await _db.AdjustPlayerRatingWithAuditAsync(targetSteamId, amount, isAdd: true, minRating: _config.MinRating, adminSteamId: adminSteamId, adminName: adminName, reason: reason);
                await MarkPlayerDirtyForWebSyncAsync(targetSteamId);
                int newRating = targetPlayer.Rating + amount;
                PrintToPlayer(slot, $"{ChatColors.Green}+{amount} rating para {targetPlayer.Name}. Novo: {newRating}.");
```

- [ ] **Step 5: Marcar dirty após `OnRatingRemove`**

Em `Commands/AdminCommands.cs:238-240`, mudar:

```csharp
                await _db.AdjustPlayerRatingWithAuditAsync(targetSteamId, amount, isAdd: false, minRating: _config.MinRating, adminSteamId: adminSteamId, adminName: adminName, reason: reason);
                int newRating = Math.Max(targetPlayer.Rating - amount, _config.MinRating);
                PrintToPlayer(slot, $"{ChatColors.Green}-{amount} rating de {targetPlayer.Name}. Novo: {newRating}.");
```

para:

```csharp
                await _db.AdjustPlayerRatingWithAuditAsync(targetSteamId, amount, isAdd: false, minRating: _config.MinRating, adminSteamId: adminSteamId, adminName: adminName, reason: reason);
                await MarkPlayerDirtyForWebSyncAsync(targetSteamId);
                int newRating = Math.Max(targetPlayer.Rating - amount, _config.MinRating);
                PrintToPlayer(slot, $"{ChatColors.Green}-{amount} rating de {targetPlayer.Name}. Novo: {newRating}.");
```

- [ ] **Step 6: Verificar que compila**

Run: `dotnet build MixRanking.csproj -c Release`
Expected: Build succeeded — vai falhar até a Task 11 atualizar o call site em `MixRankingPlugin.cs`, já que o construtor de `AdminCommands` mudou. Se a build falhar apenas por causa desse call site (erro apontando para `MixRankingPlugin.cs`), é esperado neste ponto; qualquer outro erro deve ser corrigido antes de prosseguir.

- [ ] **Step 7: Commit**

```bash
git add Commands/AdminCommands.cs
git commit -m "feat: mark players dirty for web sync on admin rating actions"
```

---

### Task 11: Ligar tudo em `MixRankingPlugin`

**Files:**
- Modify: `MixRankingPlugin.cs:1-31` (usings e campos)
- Modify: `MixRankingPlugin.cs:40-88` (`Load`)
- Modify: `MixRankingPlugin.cs:90-93` (`Unload`)

**Interfaces:**
- Consumes: `HttpWebSyncClient` (Task 8), `WebSyncService` (Task 7), `RatingService(db, config, webSyncService)` (Task 9), `AdminCommands(db, config, webSyncService)` (Task 10)

Sem teste automatizado — bootstrap do plugin requer runtime do CounterStrikeSharp. Verificação via build.

- [ ] **Step 1: Adicionar o using e os campos**

Em `MixRankingPlugin.cs:1-31`, adicionar o using e os dois novos campos:

```csharp
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using MixRanking.Commands;
using MixRanking.Config;
using MixRanking.Database;
using MixRanking.Match;
using MixRanking.Services;

namespace MixRanking;

/// <summary>
/// MixRanking — Plugin de ranking permanente para CS2.
/// Sistema Elo com swing de performance, integrado ao MatchZy.
/// </summary>
public class MixRankingPlugin : BasePlugin, IPluginConfig<RankingConfig>
{
    public override string ModuleName => "MixRanking";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "MixRanking";
    public override string ModuleDescription => "Sistema de ranking permanente baseado em Elo com swing de performance.";

    /// <summary>Configuração do plugin (carregada automaticamente do JSON).</summary>
    public RankingConfig Config { get; set; } = new();

    private DatabaseService _database = null!;
    private StatisticsService _statistics = null!;
    private RatingService _ratingService = null!;
    private PlayerService _playerService = null!;
    private MatchService _matchService = null!;
    private MatchEvents _matchEvents = null!;
    private HttpWebSyncClient _webSyncClient = null!;
    private WebSyncService _webSyncService = null!;
```

- [ ] **Step 2: Construir o `WebSyncService` antes do `RatingService` e ajustar o `AdminCommands`**

Em `MixRankingPlugin.cs:59-80`, mudar:

```csharp
        // 2. Initialize services
        _statistics = new StatisticsService();
        _ratingService = new RatingService(_database, Config);
        _playerService = new PlayerService(_database, Config.InitialRating);
        _matchService = new MatchService(_statistics, _ratingService, Config, Logger);

        // 3. Register event handlers
        _matchEvents = new MatchEvents(_matchService, _statistics, Config, Logger);
        _matchEvents.RegisterEvents(this);

        // 4. Register commands
        var rankCommand = new RankCommand(_playerService, Config);
        rankCommand.Register(this);

        var topCommand = new TopCommand(_playerService, Config);
        topCommand.Register(this);

        var statsCommand = new StatsCommand(_playerService, Config);
        statsCommand.Register(this);

        var adminCommands = new AdminCommands(_database, Config);
        adminCommands.Register(this);
```

para:

```csharp
        // 2. Initialize services
        _statistics = new StatisticsService();
        _webSyncClient = new HttpWebSyncClient(Config);
        _webSyncService = new WebSyncService(_database, _webSyncClient, Logger);
        _ratingService = new RatingService(_database, Config, _webSyncService);
        _playerService = new PlayerService(_database, Config.InitialRating);
        _matchService = new MatchService(_statistics, _ratingService, Config, Logger);

        // 3. Register event handlers
        _matchEvents = new MatchEvents(_matchService, _statistics, Config, Logger);
        _matchEvents.RegisterEvents(this);

        // 4. Register commands
        var rankCommand = new RankCommand(_playerService, Config);
        rankCommand.Register(this);

        var topCommand = new TopCommand(_playerService, Config);
        topCommand.Register(this);

        var statsCommand = new StatsCommand(_playerService, Config);
        statsCommand.Register(this);

        var adminCommands = new AdminCommands(_database, Config, _webSyncService);
        adminCommands.Register(this);
```

- [ ] **Step 3: Registrar o timer de drenagem, condicionado a config válida**

Em `MixRankingPlugin.cs:82-87` (após `_matchService.StartMatch();`, antes dos logs finais), mudar:

```csharp
        // 5. Initialize match on load (for hot reload support)
        _matchService.StartMatch();

        Logger.LogInformation("[MixRanking] Plugin loaded successfully!");
        Logger.LogInformation("[MixRanking] Commands: !rank, !top, !stats, !lastmatch, !profile");
        Logger.LogInformation("[MixRanking] Admin: !rating_set, !rating_reset, !rating_add, !rating_remove");
    }
```

para:

```csharp
        // 5. Initialize match on load (for hot reload support)
        _matchService.StartMatch();

        // 6. Web sync timer (push outbound para a plataforma web)
        if (Config.WebSyncEnabled && Config.WebSyncUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            AddTimer(Config.WebSyncIntervalSeconds, () => { _ = _webSyncService.DrainPendingAsync(); }, TimerFlags.REPEAT);
            Logger.LogInformation("[MixRanking] Web sync enabled — interval: {Interval}s", Config.WebSyncIntervalSeconds);
        }
        else
        {
            Logger.LogInformation("[MixRanking] Web sync disabled.");
        }

        Logger.LogInformation("[MixRanking] Plugin loaded successfully!");
        Logger.LogInformation("[MixRanking] Commands: !rank, !top, !stats, !lastmatch, !profile");
        Logger.LogInformation("[MixRanking] Admin: !rating_set, !rating_reset, !rating_add, !rating_remove");
    }
```

- [ ] **Step 4: Liberar o `HttpClient` no `Unload`**

Em `MixRankingPlugin.cs:90-93`, mudar:

```csharp
    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("[MixRanking] Plugin unloaded.");
    }
```

para:

```csharp
    public override void Unload(bool hotReload)
    {
        _webSyncClient.Dispose();
        Logger.LogInformation("[MixRanking] Plugin unloaded.");
    }
```

- [ ] **Step 5: Verificar que compila**

Run: `dotnet build MixRanking.csproj -c Release`
Expected: Build succeeded, sem erros.

- [ ] **Step 6: Commit**

```bash
git add MixRankingPlugin.cs
git commit -m "feat: wire web sync service and background drain timer into plugin"
```

---

### Task 12: Documentar o contrato HTTP em `docs/integration-contract.md`

**Files:**
- Modify: `docs/integration-contract.md`

Tarefa de documentação — sem código nem teste.

- [ ] **Step 1: Atualizar o cabeçalho de versão**

No topo de `docs/integration-contract.md`, mudar:

```markdown
**Document Version:** 1.0.0 (aligned with database version 4)  
**Target Database Version:** `PRAGMA user_version = 4`
```

para:

```markdown
**Document Version:** 1.1.0 (schema SQLite abaixo alinhado à versão 4; o banco está agora na versão 5, mas as tabelas novas são detalhe interno — ver seção 4)
**Target Database Version:** `PRAGMA user_version = 4` (apenas para a seção 2; ver seção 4 para o caminho ativo de integração)
```

- [ ] **Step 2: Adicionar nota de status logo abaixo do cabeçalho**

Logo após o parágrafo de introdução (antes de "## 1. Key Integration Rules"), adicionar:

```markdown
> [!NOTE]
> **Status da integração:** o caminho de leitura direta do SQLite descrito na seção 2 é
> mantido como referência de schema, mas **não é mais o mecanismo ativo de integração**
> — a topologia de produção (servidor de jogo e plataforma web em máquinas diferentes,
> sem filesystem compartilhado) o inviabiliza. A integração ativa é o sync HTTP outbound
> descrito na seção 4.
```

- [ ] **Step 3: Adicionar a seção do contrato HTTP**

No final do arquivo (depois da seção "3. Date and Time Formats"), adicionar:

```markdown

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
```

- [ ] **Step 4: Commit**

```bash
git add docs/integration-contract.md
git commit -m "docs: document outbound HTTP web sync contract"
```

---

### Task 13: Verificação final

**Files:** nenhum arquivo novo — apenas verificação.

- [ ] **Step 1: Build completo do plugin**

Run: `dotnet build MixRanking.csproj -c Release`
Expected: Build succeeded, sem erros nem warnings novos.

- [ ] **Step 2: Suíte completa de testes**

Run: `dotnet test MixRanking.Tests/MixRanking.Tests.csproj`
Expected: PASS — todos os testes do projeto (incluindo os já existentes de `RatingTests`, `StatisticsServiceTests`, `SwingCalculatorTests`, e os novos de `DatabaseTests`/`WebSyncServiceTests`/`RatingTests`).

- [ ] **Step 3: Revisar o diff completo contra o design**

Comparar as mudanças com `docs/superpowers/specs/2026-08-01-fase2-integracao-plataforma-web-design.md` — confirmar que todas as seções (Arquitetura, Componentes, Payload, Caso especial WIPE, Segurança, Contrato HTTP, Tratamento de erros, Testes) têm uma tarefa correspondente implementada.

Se tudo estiver coberto e os testes passarem, o item "Construir a integração real com a plataforma web (rating → nível)" pode ser marcado como concluído no `TODO.md` (fora do escopo deste plano — decisão do usuário).
