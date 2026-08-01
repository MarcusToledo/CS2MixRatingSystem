# Fase 1 — Lançamento Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Corrigir os 4 bugs listados em `TODO.md` sob "Fase 1 — Lançamento": ADR aproximado no `!rank`/`!stats`, double counting estrutural no cálculo de KAST, `journal_mode` do SQLite não configurado, e o padrão `Task.Run(...).Wait()` síncrono na inicialização do plugin.

**Architecture:** Nenhuma mudança de schema é necessária (schema permanece na versão 4). Task 1 usa uma nova query agregada sobre `match_player_stats` (a tabela detalhada já criada na Fase 0) em vez de aproximar rounds. Task 2 substitui a soma de contadores potencialmente sobrepostos por um contador único (`RoundsWithKast`) calculado por round em `StatisticsService`, a mesma fonte de verdade que já popula `match_player_stats`. Tasks 3 e 4 são mudanças isoladas em `DatabaseService`/`MixRankingPlugin`.

**Tech Stack:** C# / .NET 8, Microsoft.Data.Sqlite, CounterStrikeSharp.API, xUnit.

## Global Constraints

- .NET 8, `Nullable` e `ImplicitUsings` habilitados (ver `MixRanking.csproj`).
- Comentários mínimos — só quando o "porquê" não é óbvio, seguindo o padrão já existente no repositório.
- Testes em `MixRanking.Tests` usando xUnit (`[Fact]`), seguindo o padrão de `DatabaseTests.cs`/`RatingTests.cs`: `SqliteConnection` em memória com `Mode=Memory;Cache=Shared`, uma conexão "keep-alive" no construtor para manter o DB vivo entre aberturas do `DatabaseService`.
- `DatabaseService` abre uma nova `SqliteConnection` por método — padrão já existente no projeto (listado como débito técnico separado em `TODO.md`), não mexer nisso agora.
- Nenhuma das 4 tarefas requer migração — `CurrentSchemaVersion` permanece em `4`.
- Rodar `dotnet test` após cada tarefa, antes de commitar.
- Escopo do fix de KAST (Task 2) foi ampliado após investigação e confirmação do usuário: além de deduplicar `RoundsWithKill`/`RoundsSurvived` no mesmo round, corrige o crédito de "Traded" para o jogador que morreu e foi vingado (não para quem vingou, que já é contado via kill), e remove a contagem redundante de `TradeKills` na fórmula.

---

### Task 1: Corrigir ADR aproximado no `!rank` e `!stats`

**Files:**
- Modify: `Database/DatabaseService.cs` (novo método, após `GetTotalRankedPlayersAsync`, ~linha 398)
- Modify: `Services/PlayerService.cs` (passthrough)
- Modify: `Commands/RankCommand.cs:36-69`
- Modify: `Commands/StatsCommand.cs:38-58`
- Test: `MixRanking.Tests/DatabaseTests.cs`

**Interfaces:**
- Produces: `DatabaseService.GetPlayerTotalRoundsPlayedAsync(string steamId) : Task<int>`
- Produces: `PlayerService.GetTotalRoundsPlayedAsync(string steamId) : Task<int>`

- [ ] **Step 1: Escrever o teste que falha**

Adicionar em `MixRanking.Tests/DatabaseTests.cs`, após o método `WriteMatchEndResultAsync_SavesSuccessfullyInSingleTransaction`:

```csharp
[Fact]
public async Task GetPlayerTotalRoundsPlayedAsync_SumsRoundsAcrossMatches()
{
    await _db.InitializeAsync();
    int activeSeasonId = await _db.GetActiveSeasonIdAsync();

    async Task WriteMatch(int roundsPlayed)
    {
        var match = new MatchRecord
        {
            MatchGuid = Guid.NewGuid().ToString(),
            Map = "de_inferno",
            WinnerTeam = 3,
            CtScore = 13,
            TScore = 5,
            FinishedAt = DateTime.UtcNow
        };

        var stats = new MatchPlayerStats
        {
            SteamId = 76561198000000050,
            PlayerName = "RoundsPlayer",
            Team = CsTeam.CounterTerrorist,
            Kills = 10,
            Deaths = 10,
            Damage = 1000,
            RoundsPlayed = roundsPlayed,
            RoundsSurvived = 5,
            RoundsWithKill = 5,
            Abandoned = false
        };

        var playerData = new PlayerData { SteamId = "76561198000000050", Name = "RoundsPlayer", Rating = 1000, Matches = 0 };
        var ratingChange = new RatingChange
        {
            SteamId = "76561198000000050",
            OldRating = 1000,
            BaseChange = 10,
            PerformanceSwing = 0,
            TotalChange = 10,
            NewRating = 1010,
            PlayerName = "RoundsPlayer",
            Won = true,
            KFactorUsed = 50
        };

        var update = new MatchPlayerUpdate { Stats = stats, PlayerData = playerData, RatingChange = ratingChange, NewRating = 1010, Won = true };
        await _db.WriteMatchEndResultAsync(match, new List<MatchPlayerUpdate> { update }, 1000, activeSeasonId);
    }

    // Act
    await WriteMatch(roundsPlayed: 21);
    await WriteMatch(roundsPlayed: 16);

    // Assert
    int totalRounds = await _db.GetPlayerTotalRoundsPlayedAsync("76561198000000050");
    Assert.Equal(37, totalRounds);
}

[Fact]
public async Task GetPlayerTotalRoundsPlayedAsync_ReturnsZero_WhenPlayerHasNoMatches()
{
    await _db.InitializeAsync();

    int totalRounds = await _db.GetPlayerTotalRoundsPlayedAsync("76561198099999999");

    Assert.Equal(0, totalRounds);
}
```

- [ ] **Step 2: Rodar os testes e confirmar que falham (erro de compilação)**

Run: `dotnet test`
Expected: FAIL com erro de compilação — `'DatabaseService' does not contain a definition for 'GetPlayerTotalRoundsPlayedAsync'`.

- [ ] **Step 3: Implementar `GetPlayerTotalRoundsPlayedAsync` em `DatabaseService.cs`**

Adicionar logo após `GetTotalRankedPlayersAsync` (antes de `GetPlayersBySteamIdsAsync`):

```csharp
    /// <summary>Retorna o total de rounds jogados acumulados de um jogador, somando todas as partidas registradas.</summary>
    public async Task<int> GetPlayerTotalRoundsPlayedAsync(string steamId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(rounds_played), 0) FROM match_player_stats WHERE steamid = $steamId";
        command.Parameters.AddWithValue("$steamId", steamId);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }
```

E em `Services/PlayerService.cs`, adicionar após `GetTotalRankedPlayersAsync`:

```csharp
    /// <summary>Retorna o total de rounds jogados acumulados de um jogador (soma de match_player_stats).</summary>
    public Task<int> GetTotalRoundsPlayedAsync(string steamId)
        => _db.GetPlayerTotalRoundsPlayedAsync(steamId);
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test --filter GetPlayerTotalRoundsPlayedAsync`
Expected: PASS (2 testes).

- [ ] **Step 5: Corrigir `Commands/RankCommand.cs`**

Substituir o bloco (linhas 36-49 e 61-62):

```csharp
        Task.Run(async () =>
        {
            try
            {
                var playerData = await _playerService.GetPlayerAsync(steamId);
                int rankPosition = 0;
                int totalPlayers = 0;

                if (playerData != null && playerData.Matches > 0)
                {
                    rankPosition = await _playerService.GetRankPositionAsync(steamId);
                    totalPlayers = await _playerService.GetTotalRankedPlayersAsync();
                }
```

por:

```csharp
        Task.Run(async () =>
        {
            try
            {
                var playerData = await _playerService.GetPlayerAsync(steamId);
                int rankPosition = 0;
                int totalPlayers = 0;
                int totalRounds = 0;

                if (playerData != null && playerData.Matches > 0)
                {
                    rankPosition = await _playerService.GetRankPositionAsync(steamId);
                    totalPlayers = await _playerService.GetTotalRankedPlayersAsync();
                    totalRounds = await _playerService.GetTotalRoundsPlayedAsync(steamId);
                }
```

E substituir a linha 62:

```csharp
                    double adr = playerData.Matches > 0 ? (double)playerData.Damage / (playerData.Matches * 24) : 0; // Approximate 24 rounds per match
```

por:

```csharp
                    double adr = totalRounds > 0 ? (double)playerData.Damage / totalRounds : 0;
```

- [ ] **Step 6: Corrigir `Commands/StatsCommand.cs`**

Substituir o bloco (linhas 38-40):

```csharp
                var playerData = await _playerService.GetPlayerAsync(steamId);

                Server.NextFrame(() =>
```

por:

```csharp
                var playerData = await _playerService.GetPlayerAsync(steamId);
                int totalRounds = playerData != null && playerData.Matches > 0
                    ? await _playerService.GetTotalRoundsPlayedAsync(steamId)
                    : 0;

                Server.NextFrame(() =>
```

E substituir as linhas 57-58:

```csharp
                    // Approximate total rounds (average 24 rounds per match)
                    long estimatedRounds = playerData.Matches * 24L;
                    double adr = estimatedRounds > 0 ? (double)playerData.Damage / estimatedRounds : 0;
```

por:

```csharp
                    double adr = totalRounds > 0 ? (double)playerData.Damage / totalRounds : 0;
```

- [ ] **Step 7: Rodar toda a suíte de testes**

Run: `dotnet test`
Expected: PASS (todos os testes, incluindo os novos).

- [ ] **Step 8: Commit**

```bash
git add Database/DatabaseService.cs Services/PlayerService.cs Commands/RankCommand.cs Commands/StatsCommand.cs MixRanking.Tests/DatabaseTests.cs
git commit -m "fix: use real cumulative rounds for ADR in !rank and !stats"
```

---

### Task 2: Corrigir cálculo estrutural do KAST (double counting + crédito de Traded)

**Files:**
- Modify: `Models/MatchPlayerStats.cs`
- Modify: `Services/StatisticsService.cs`
- Modify: `Rating/SwingCalculator.cs`
- Modify: `MixRanking.Tests/RatingTests.cs`
- Modify: `MixRanking.Tests/DatabaseTests.cs`
- Create: `MixRanking.Tests/StatisticsServiceTests.cs`
- Create: `MixRanking.Tests/SwingCalculatorTests.cs`

**Interfaces:**
- Consumes: `MatchPlayerStats.RoundsPlayed`, `.RoundsWithKill`, `.RoundsSurvived` (existentes)
- Produces: `MatchPlayerStats.RoundsWithKast : int` (novo, cumulativo)
- Produces: `MatchPlayerStats.GotAssistThisRound : bool`, `.WasTradedThisRound : bool` (novos, resetados por round)
- Produces: `SwingCalculator.CalculateKastPercent(MatchPlayerStats)` passa a usar `RoundsWithKast` em vez de somar `RoundsWithKill + Assists*0.5 + RoundsSurvived + TradeKills`

- [ ] **Step 1: Escrever o teste que falha — dedup de kill+survive no mesmo round**

Criar `MixRanking.Tests/StatisticsServiceTests.cs`:

```csharp
using CounterStrikeSharp.API.Modules.Utils;
using MixRanking.Services;
using Xunit;

namespace MixRanking.Tests;

public class StatisticsServiceTests
{
    private const ulong AttackerSteamId = 76561198000000010;
    private const ulong VictimSteamId = 76561198000000020;
    private const ulong TraderSteamId = 76561198000000030;

    [Fact]
    public void OnRoundEnd_DoesNotDoubleCount_WhenPlayerBothKillsAndSurvives()
    {
        var service = new StatisticsService();
        service.GetOrCreateStats(AttackerSteamId, "Attacker", CsTeam.CounterTerrorist);
        service.GetOrCreateStats(VictimSteamId, "Victim", CsTeam.Terrorist);

        service.OnRoundStart();
        service.RecordKill(AttackerSteamId, VictimSteamId, CsTeam.Terrorist, false);
        service.OnRoundEnd(new[] { AttackerSteamId });

        var stats = service.GetAllPlayerStats()[AttackerSteamId];

        Assert.Equal(1, stats.RoundsWithKill);
        Assert.Equal(1, stats.RoundsSurvived);
        Assert.Equal(1, stats.RoundsWithKast);
    }

    [Fact]
    public void OnRoundEnd_CreditsAssistTowardKast()
    {
        var service = new StatisticsService();
        ulong killerSteamId = 76561198000000040;
        service.GetOrCreateStats(killerSteamId, "Killer", CsTeam.CounterTerrorist);
        service.GetOrCreateStats(AttackerSteamId, "Assister", CsTeam.CounterTerrorist);
        service.GetOrCreateStats(VictimSteamId, "Victim", CsTeam.Terrorist);

        service.OnRoundStart();
        service.RecordAssist(AttackerSteamId, false);
        service.RecordKill(killerSteamId, VictimSteamId, CsTeam.Terrorist, false);
        service.OnRoundEnd(new[] { killerSteamId });

        var stats = service.GetAllPlayerStats()[AttackerSteamId];

        Assert.Equal(1, stats.RoundsWithKast);
    }

    [Fact]
    public void RecordKill_CreditsTradedVictim_NotJustTheAvenger()
    {
        var service = new StatisticsService();
        service.GetOrCreateStats(AttackerSteamId, "Attacker", CsTeam.Terrorist);
        service.GetOrCreateStats(VictimSteamId, "Victim", CsTeam.CounterTerrorist);
        service.GetOrCreateStats(TraderSteamId, "Trader", CsTeam.CounterTerrorist);

        service.OnRoundStart();

        // Attacker mata Victim primeiro
        service.RecordKill(AttackerSteamId, VictimSteamId, CsTeam.CounterTerrorist, false);
        // Trader (teammate de Victim) vinga Victim matando Attacker
        service.RecordKill(TraderSteamId, AttackerSteamId, CsTeam.Terrorist, false);

        // Só Trader está vivo no fim do round
        service.OnRoundEnd(new[] { TraderSteamId });

        var victimStats = service.GetAllPlayerStats()[VictimSteamId];
        var traderStats = service.GetAllPlayerStats()[TraderSteamId];

        // Victim morreu mas foi vingado -> ainda recebe crédito de KAST
        Assert.Equal(1, victimStats.RoundsWithKast);
        // Trader recebe crédito pelo próprio kill, não por um bônus redundante de trade
        Assert.Equal(1, traderStats.RoundsWithKast);
        Assert.Equal(1, traderStats.TradeKills);
    }
}
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `dotnet test --filter StatisticsServiceTests`
Expected: FAIL com erro de compilação — `'MatchPlayerStats' does not contain a definition for 'RoundsWithKast'`.

- [ ] **Step 3: Adicionar os novos campos em `Models/MatchPlayerStats.cs`**

Substituir o bloco (linhas 47-58, entre `TradeKills`/`FlashAssists` e `WasAliveAtRoundStart`):

```csharp
    /// <summary>Trade kills (kill em menos de 5s após teammate morrer).</summary>
    public int TradeKills { get; set; }

    /// <summary>Flash assists.</summary>
    public int FlashAssists { get; set; }

    /// <summary>Se o jogador estava vivo no início do round atual.</summary>
    public bool WasAliveAtRoundStart { get; set; }

    /// <summary>Se o jogador fez kill neste round.</summary>
    public bool GotKillThisRound { get; set; }
```

por:

```csharp
    /// <summary>Trade kills (kill em menos de 5s após teammate morrer).</summary>
    public int TradeKills { get; set; }

    /// <summary>Flash assists.</summary>
    public int FlashAssists { get; set; }

    /// <summary>Rounds em que o jogador teve pelo menos um evento de KAST (Kill, Assist, Survived ou Traded), sem contagem duplicada.</summary>
    public int RoundsWithKast { get; set; }

    /// <summary>Se o jogador estava vivo no início do round atual.</summary>
    public bool WasAliveAtRoundStart { get; set; }

    /// <summary>Se o jogador fez kill neste round.</summary>
    public bool GotKillThisRound { get; set; }

    /// <summary>Se o jogador deu assistência neste round.</summary>
    public bool GotAssistThisRound { get; set; }

    /// <summary>Se a morte do jogador neste round foi vingada por um teammate dentro da janela de trade.</summary>
    public bool WasTradedThisRound { get; set; }
```

E atualizar `ResetRoundFlags()`:

```csharp
    /// <summary>Reseta flags por round.</summary>
    public void ResetRoundFlags()
    {
        WasAliveAtRoundStart = true;
        GotKillThisRound = false;
        GotAssistThisRound = false;
        WasTradedThisRound = false;
    }
```

- [ ] **Step 4: Implementar o crédito de "Traded" e o assist flag em `Services/StatisticsService.cs`**

No método `RecordKill`, substituir o loop de detecção de trade kill (linhas 60-73):

```csharp
            var now = DateTime.UtcNow;
            for (int i = _recentDeaths.Count - 1; i >= 0; i--)
            {
                var death = _recentDeaths[i];
                if ((now - death.Time).TotalSeconds > 5.0)
                    break;

                // If the victim of the recent death was on the same team as the attacker,
                // this is a trade kill (attacker traded for their fallen teammate)
                if (death.VictimTeam == attackerStats.Team && death.VictimSteamId != attackerSteamId)
                {
                    attackerStats.TradeKills++;
                    break;
                }
            }
```

por:

```csharp
            var now = DateTime.UtcNow;
            for (int i = _recentDeaths.Count - 1; i >= 0; i--)
            {
                var death = _recentDeaths[i];
                if ((now - death.Time).TotalSeconds > 5.0)
                    break;

                // If the victim of the recent death was on the same team as the attacker,
                // this is a trade kill (attacker traded for their fallen teammate).
                // The teammate who died gets KAST credit for being traded — the avenger
                // is already credited via GotKillThisRound, so it isn't counted twice.
                if (death.VictimTeam == attackerStats.Team && death.VictimSteamId != attackerSteamId)
                {
                    attackerStats.TradeKills++;
                    if (_playerStats.TryGetValue(death.VictimSteamId, out var tradedStats))
                    {
                        tradedStats.WasTradedThisRound = true;
                    }
                    break;
                }
            }
```

No método `RecordAssist`, substituir:

```csharp
    public void RecordAssist(ulong assistSteamId, bool flashAssist)
    {
        if (_playerStats.TryGetValue(assistSteamId, out var stats))
        {
            stats.Assists++;
            if (flashAssist)
                stats.FlashAssists++;
        }
    }
```

por:

```csharp
    public void RecordAssist(ulong assistSteamId, bool flashAssist)
    {
        if (_playerStats.TryGetValue(assistSteamId, out var stats))
        {
            stats.Assists++;
            stats.GotAssistThisRound = true;
            if (flashAssist)
                stats.FlashAssists++;
        }
    }
```

No método `OnRoundEnd`, substituir:

```csharp
    public void OnRoundEnd(IEnumerable<ulong> aliveSteamIds)
    {
        var aliveSet = new HashSet<ulong>(aliveSteamIds);

        foreach (var stats in _playerStats.Values)
        {
            if (aliveSet.Contains(stats.SteamId))
            {
                stats.RoundsSurvived++;
            }

            if (stats.GotKillThisRound)
            {
                stats.RoundsWithKill++;
            }
        }
    }
```

por:

```csharp
    public void OnRoundEnd(IEnumerable<ulong> aliveSteamIds)
    {
        var aliveSet = new HashSet<ulong>(aliveSteamIds);

        foreach (var stats in _playerStats.Values)
        {
            bool survived = aliveSet.Contains(stats.SteamId);
            if (survived)
            {
                stats.RoundsSurvived++;
            }

            if (stats.GotKillThisRound)
            {
                stats.RoundsWithKill++;
            }

            // União, não soma: um round só conta uma vez para KAST mesmo que o
            // jogador tenha matado, sobrevivido, assistido e sido vingado ao mesmo tempo.
            if (stats.GotKillThisRound || survived || stats.GotAssistThisRound || stats.WasTradedThisRound)
            {
                stats.RoundsWithKast++;
            }
        }
    }
```

- [ ] **Step 5: Rodar os novos testes e confirmar que passam**

Run: `dotnet test --filter StatisticsServiceTests`
Expected: PASS (3 testes).

- [ ] **Step 6: Reescrever `CalculateKastPercent` em `Rating/SwingCalculator.cs`**

Substituir (linhas 73-87):

```csharp
    /// <summary>
    /// Calcula o percentual de KAST (% de rounds com Kill, Assist, Survived ou Traded).
    /// Compartilhado entre o cálculo de swing e a persistência de match_player_stats,
    /// para que uma futura correção do double-counting não precise ser replicada.
    /// </summary>
    public static double CalculateKastPercent(MatchPlayerStats stats)
    {
        if (stats.RoundsPlayed <= 0) return 0.0;

        // KAST = % of rounds with Kill, Assist, Survived, or Traded
        // Using RoundsWithKill + assists contribution + survived
        double kastRounds = stats.RoundsWithKill + (stats.Assists * 0.5) + stats.RoundsSurvived + stats.TradeKills;
        // Avoid double counting: cap at rounds played
        return Math.Min(kastRounds / stats.RoundsPlayed, 1.0);
    }
```

por:

```csharp
    /// <summary>
    /// Calcula o percentual de KAST (% de rounds com Kill, Assist, Survived ou Traded).
    /// RoundsWithKast já é a contagem de rounds única (sem overlap) — ver StatisticsService.OnRoundEnd.
    /// Compartilhado entre o cálculo de swing e a persistência de match_player_stats.
    /// </summary>
    public static double CalculateKastPercent(MatchPlayerStats stats)
    {
        if (stats.RoundsPlayed <= 0) return 0.0;

        return Math.Min((double)stats.RoundsWithKast / stats.RoundsPlayed, 1.0);
    }
```

- [ ] **Step 7: Criar `MixRanking.Tests/SwingCalculatorTests.cs`**

```csharp
using CounterStrikeSharp.API.Modules.Utils;
using MixRanking.Models;
using MixRanking.Rating;
using Xunit;

namespace MixRanking.Tests;

public class SwingCalculatorTests
{
    [Fact]
    public void CalculateKastPercent_ReturnsZero_WhenNoRoundsPlayed()
    {
        var stats = new MatchPlayerStats
        {
            SteamId = 1,
            Team = CsTeam.CounterTerrorist,
            RoundsPlayed = 0,
            RoundsWithKast = 0
        };

        double result = SwingCalculator.CalculateKastPercent(stats);

        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CalculateKastPercent_DividesKastRoundsByRoundsPlayed()
    {
        var stats = new MatchPlayerStats
        {
            SteamId = 1,
            Team = CsTeam.CounterTerrorist,
            RoundsPlayed = 20,
            RoundsWithKast = 15
        };

        double result = SwingCalculator.CalculateKastPercent(stats);

        Assert.Equal(0.75, result, 3);
    }

    [Fact]
    public void CalculateKastPercent_ClampsAtOneHundredPercent()
    {
        var stats = new MatchPlayerStats
        {
            SteamId = 1,
            Team = CsTeam.CounterTerrorist,
            RoundsPlayed = 10,
            RoundsWithKast = 12
        };

        double result = SwingCalculator.CalculateKastPercent(stats);

        Assert.Equal(1.0, result);
    }
}
```

- [ ] **Step 8: Atualizar os testes existentes que dependiam da fórmula antiga de KAST**

Em `MixRanking.Tests/RatingTests.cs`, no método `ProcessMatchEndAsync_AppliesPlacementDoubleKFactor`, adicionar `RoundsWithKast = 15,` ao objeto `stats` (logo após `RoundsWithKill = 10,`):

```csharp
            RoundsPlayed = 20,
            RoundsSurvived = 5,
            RoundsWithKill = 10,
            RoundsWithKast = 15,
            Mvps = 0,
```

E atualizar o comentário (linha ~90):

```csharp
        // KAST = RoundsWithKast/RoundsPlayed = 15/20 = 0.75 (kastScore = (75 - 50)/(90 - 50) = 25/40 = 0.625)
```

No método `ProcessMatchEndAsync_AppliesStandardKFactorAfterPlacement`, adicionar a mesma linha `RoundsWithKast = 15,` ao objeto `stats` equivalente (logo após `RoundsWithKill = 10,`).

Em `MixRanking.Tests/DatabaseTests.cs`, no método `WriteMatchEndResultAsync_SavesSuccessfullyInSingleTransaction`, adicionar `RoundsWithKast = 21,` ao objeto `stats` (logo após `RoundsWithKill = 12,`):

```csharp
            RoundsPlayed = 21,
            RoundsSurvived = 11,
            RoundsWithKill = 12,
            RoundsWithKast = 21,
            Mvps = 3,
```

E atualizar o comentário (linha ~147):

```csharp
            // KAST = RoundsWithKast/RoundsPlayed, capped at 1.0 (every round contributed here)
```

- [ ] **Step 9: Rodar toda a suíte de testes**

Run: `dotnet test`
Expected: PASS (todos os testes).

- [ ] **Step 10: Commit**

```bash
git add Models/MatchPlayerStats.cs Services/StatisticsService.cs Rating/SwingCalculator.cs MixRanking.Tests/RatingTests.cs MixRanking.Tests/DatabaseTests.cs MixRanking.Tests/StatisticsServiceTests.cs MixRanking.Tests/SwingCalculatorTests.cs
git commit -m "fix: eliminate KAST double counting and credit traded victims correctly"
```

---

### Task 3: Habilitar `PRAGMA journal_mode=WAL`

**Files:**
- Modify: `Database/DatabaseService.cs:145-155` (`InitializeAsync`)
- Test: `MixRanking.Tests/DatabaseTests.cs`

**Interfaces:**
- N/A (mudança interna, sem novos métodos públicos)

- [ ] **Step 1: Escrever o teste que falha**

`Mode=Memory` do SQLite não suporta WAL (a pragma retorna `memory` nesse modo), então o teste precisa usar um arquivo real em disco, não a conexão em memória compartilhada usada no resto da suíte. Adicionar em `MixRanking.Tests/DatabaseTests.cs`, após `InitializeAsync_RunsMigrationsAndSetsUserVersion`:

```csharp
[Fact]
public async Task InitializeAsync_EnablesWalJournalMode()
{
    string tempDbPath = Path.Combine(Path.GetTempPath(), $"mixranking_test_{Guid.NewGuid():N}.db");
    var fileDb = new DatabaseService(tempDbPath);

    try
    {
        await fileDb.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={tempDbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var mode = (string)(await command.ExecuteScalarAsync())!;

        Assert.Equal("wal", mode, ignoreCase: true);
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        File.Delete(tempDbPath);
        if (File.Exists(tempDbPath + "-wal")) File.Delete(tempDbPath + "-wal");
        if (File.Exists(tempDbPath + "-shm")) File.Delete(tempDbPath + "-shm");
    }
}
```

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `dotnet test --filter InitializeAsync_EnablesWalJournalMode`
Expected: FAIL — `mode` retorna `"delete"` (journal mode padrão do SQLite), não `"wal"`.

- [ ] **Step 3: Habilitar WAL em `InitializeAsync`**

Substituir o início do método (linhas 145-149):

```csharp
    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        int version = await GetUserVersionAsync(connection);
```

por:

```csharp
    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var walCommand = connection.CreateCommand();
        walCommand.CommandText = "PRAGMA journal_mode=WAL;";
        await walCommand.ExecuteNonQueryAsync();

        int version = await GetUserVersionAsync(connection);
```

`journal_mode=WAL` é uma propriedade persistida no cabeçalho do arquivo do banco, então basta configurá-la uma vez em `InitializeAsync` — conexões abertas depois (inclusive as que o resto do `DatabaseService` abre por método) já herdam o modo WAL do arquivo.

- [ ] **Step 4: Rodar o teste e confirmar que passa**

Run: `dotnet test --filter InitializeAsync_EnablesWalJournalMode`
Expected: PASS.

- [ ] **Step 5: Rodar toda a suíte de testes**

Run: `dotnet test`
Expected: PASS (todos os testes — os testes em memória continuam funcionando normalmente, já que `PRAGMA journal_mode=WAL` em um DB `:memory:` é uma operação inofensiva que apenas mantém o modo `memory`).

- [ ] **Step 6: Commit**

```bash
git add Database/DatabaseService.cs MixRanking.Tests/DatabaseTests.cs
git commit -m "fix: enable WAL journal mode for SQLite database"
```

---

### Task 4: Remover `Task.Run(...).Wait()` síncrono no `Load()` do plugin

**Files:**
- Modify: `MixRankingPlugin.cs:40-90`

**Interfaces:**
- N/A (mudança estrutural interna ao método `Load`)

**Nota:** `MixRankingPlugin` herda de `BasePlugin` do CounterStrikeSharp e depende de `ModuleDirectory`/`Logger`, que só existem dentro do host do jogo — não há como instanciar essa classe em um teste xUnit isolado (mesma limitação já registrada em `TODO.md` sob "Débitos técnicos: Nenhum projeto de teste automatizado"). Por isso este task não segue o ciclo padrão de teste-primeiro; a verificação é build + suíte completa + smoke test manual.

- [ ] **Step 1: Remover o `Task.Run(...).Wait()`**

Substituir em `MixRankingPlugin.cs` (linhas 48-59):

```csharp
        Task.Run(async () =>
        {
            try
            {
                await _database.InitializeAsync();
                Logger.LogInformation("[MixRanking] Database initialized at {Path}", dbPath);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[MixRanking] Failed to initialize database!");
            }
        }).Wait();
```

por:

```csharp
        try
        {
            _database.InitializeAsync().GetAwaiter().GetResult();
            Logger.LogInformation("[MixRanking] Database initialized at {Path}", dbPath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[MixRanking] Failed to initialize database!");
        }
```

`Task.Run(...).Wait()` empurrava a inicialização para uma thread do thread pool só para em seguida bloquear a thread chamadora esperando por ela — uma indireção sem benefício. `Microsoft.Data.Sqlite` não faz I/O assíncrono de verdade (os métodos `*Async` completam de forma síncrona), então `GetAwaiter().GetResult()` direto não arrisca deadlock por captura de `SynchronizationContext` e evita o hop desnecessário pelo thread pool.

- [ ] **Step 2: Compilar o projeto**

Run: `dotnet build`
Expected: Build succeeded, sem warnings novos.

- [ ] **Step 3: Rodar toda a suíte de testes**

Run: `dotnet test`
Expected: PASS (todos os testes — nenhum teste cobre `MixRankingPlugin.Load` diretamente, então isso confirma apenas que nada mais quebrou).

- [ ] **Step 4: Commit**

```bash
git add MixRankingPlugin.cs
git commit -m "fix: remove Task.Run(...).Wait() from plugin Load()"
```

---

## Self-Review

**1. Cobertura do escopo:** os 4 itens de "Fase 1 — Lançamento" do `TODO.md` têm task correspondente (Task 1 = ADR, Task 2 = KAST, Task 3 = WAL, Task 4 = `Task.Run().Wait()`). O escopo de KAST foi ampliado com confirmação explícita do usuário para incluir o crédito de "Traded" à vítima, não apenas a soma do avenger.

**2. Placeholders:** nenhum "TBD"/"implementar depois" — todos os steps têm código completo e comandos de verificação com resultado esperado explícito.

**3. Consistência de tipos:** `RoundsWithKast` é `int` em todas as ocorrências (model, testes, `SwingCalculator`). `GetPlayerTotalRoundsPlayedAsync`/`GetTotalRoundsPlayedAsync` usam a mesma assinatura `(string steamId) : Task<int>` em `DatabaseService` e `PlayerService`. `GotAssistThisRound`/`WasTradedThisRound` são resetados em `ResetRoundFlags()`, mesma convenção de `GotKillThisRound`.

**4. Após Fase 1:** atualizar `TODO.md` marcando os 4 itens de "Fase 1 — Lançamento" como concluídos, análogo ao que foi feito para a Fase 0 (fora do escopo desta implementação — decisão do usuário quando as 4 tasks estiverem commitadas).
