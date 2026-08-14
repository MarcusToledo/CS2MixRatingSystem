using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MixRanking.Config;
using MixRanking.Models;
using MixRanking.Services;
using CounterStrikeSharp.API.Modules.Utils;
using Xunit;

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
            AbandonPenalty = 15,
            MinRatingChangeMagnitude = 3
        };

        _webSyncService = new WebSyncService(_db, new FakeWebSyncClient(), NullLogger.Instance);
        _ratingService = new RatingService(_db, _config, _webSyncService, NullLogger.Instance);
    }

    [Fact]
    public async Task ProcessMatchEndAsync_AppliesPlacementDoubleKFactor()
    {
        // Arrange: Player has 0 matches (less than 10)
        var stats = new MatchPlayerStats
        {
            SteamId = 76561198000000100,
            PlayerName = "PlacementPlayer",
            Team = CsTeam.CounterTerrorist,
            Kills = 15,
            Deaths = 15,
            Assists = 0,
            Damage = 1500,
            RoundsPlayed = 20,
            RoundsSurvived = 5,
            RoundsWithKill = 10,
            RoundsWithKast = 15,
            Mvps = 0,
            OpeningKills = 0,
            OpeningDeaths = 0,
            TradeKills = 0,
            FlashAssists = 0,
            Abandoned = false
        };

        var playerStats = new Dictionary<ulong, MatchPlayerStats>
        {
            { 76561198000000100, stats }
        };

        // Act
        var changes = await _ratingService.ProcessMatchEndAsync(
            Guid.NewGuid().ToString(), "de_dust2", CsTeam.CounterTerrorist, 13, 7, playerStats);

        // Assert
        Assert.Single(changes);
        var change = changes[0];
        // Effective K factor should be 50 * 2.0 = 100
        Assert.Equal(100, change.KFactorUsed);

        // Expected expectedScore for 1000 avg vs 1000 avg is 0.5.
        // Base change = 100 * (1 - 0.5) = 50.
        // Performance score for stats:
        // ADR = 1500 / 20 = 75 (adrScore = (75 - 40)/(120 - 40) = 35/80 = 0.4375)
        // KAST = RoundsWithKast/RoundsPlayed = 15/20 = 0.75 (kastScore = (75 - 50)/(90 - 50) = 25/40 = 0.625)
        // KPR = 15/20 = 0.75 (kprScore = (0.75 - 0.3)/(1.2 - 0.3) = 0.45/0.9 = 0.5)
        // KD = 15/15 = 1.0 (kdScore = (1.0 - 0.5)/(2.0 - 0.5) = 0.5/1.5 = 0.333)
        // MVP = 0 (mvpScore = 0)
        // Weighted performance score ~ 0.475
        // Swing ~ (0.475 - 0.5) * 2 * 7 = -0.35 -> Rounded to 0
        // Total change ~ 50 + 0 = 50.
        Assert.Equal(50, change.BaseChange);
        Assert.Equal(50, change.TotalChange);
        Assert.Equal(1050, change.NewRating);
    }

    [Fact]
    public async Task ProcessMatchEndAsync_AppliesStandardKFactorAfterPlacement()
    {
        // Arrange: Player already has 10 matches in DB
        // Insert player with 10 matches
        _db.Players["76561198000000200"] = new PlayerData
        {
            SteamId = "76561198000000200", Name = "ExperiencedPlayer",
            Rating = 1200, Matches = 10, Wins = 5, Losses = 5
        };

        var stats = new MatchPlayerStats
        {
            SteamId = 76561198000000200,
            PlayerName = "ExperiencedPlayer",
            Team = CsTeam.CounterTerrorist,
            Kills = 15,
            Deaths = 15,
            Assists = 0,
            Damage = 1500,
            RoundsPlayed = 20,
            RoundsSurvived = 5,
            RoundsWithKill = 10,
            RoundsWithKast = 15,
            Mvps = 0,
            OpeningKills = 0,
            OpeningDeaths = 0,
            TradeKills = 0,
            FlashAssists = 0,
            Abandoned = false
        };

        var playerStats = new Dictionary<ulong, MatchPlayerStats>
        {
            { 76561198000000200, stats }
        };

        // Act
        var changes = await _ratingService.ProcessMatchEndAsync(
            Guid.NewGuid().ToString(), "de_dust2", CsTeam.CounterTerrorist, 13, 7, playerStats);

        // Assert
        Assert.Single(changes);
        var change = changes[0];
        // Effective K factor should be standard 50
        Assert.Equal(50, change.KFactorUsed);
        // Base change = 50 * (1 - 0.7597) = 12
        Assert.Equal(12, change.BaseChange);
        Assert.Equal(1212, change.NewRating);
    }

    [Fact]
    public async Task ProcessMatchEndAsync_MarksUpdatedPlayerForWebSync()
    {
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

    [Fact]
    public async Task ProcessMatchEndAsync_GuaranteesPositiveChange_WhenHeavyFavoriteWinsWithPoorPerformance()
    {
        // Arrange: time CT muito favorito (1400) vence o time T (1000), mas o
        // jogador do CT tem uma performance ruim o bastante para, sem a garantia
        // de piso mínimo, resultar em 0 pontos (ou até negativo) apesar da vitória
        // — reproduz exatamente o bug relatado.
        _db.Players["76561198000000400"] = new PlayerData
        {
            SteamId = "76561198000000400", Name = "FavoriteButBadPerformance",
            Rating = 1400, Matches = 10, Wins = 5, Losses = 5
        };
        _db.Players["76561198000000401"] = new PlayerData
        {
            SteamId = "76561198000000401", Name = "Underdog",
            Rating = 1000, Matches = 10, Wins = 5, Losses = 5
        };

        var badPerformer = new MatchPlayerStats
        {
            SteamId = 76561198000000400,
            PlayerName = "FavoriteButBadPerformance",
            Team = CsTeam.CounterTerrorist,
            Kills = 6,
            Deaths = 20,
            Assists = 0,
            Damage = 800,
            RoundsPlayed = 20,
            RoundsSurvived = 0,
            RoundsWithKill = 5,
            RoundsWithKast = 6,
            Mvps = 0,
            Abandoned = false
        };
        var opponent = new MatchPlayerStats
        {
            SteamId = 76561198000000401,
            PlayerName = "Underdog",
            Team = CsTeam.Terrorist,
            Kills = 15,
            Deaths = 15,
            Assists = 0,
            Damage = 1500,
            RoundsPlayed = 20,
            RoundsSurvived = 5,
            RoundsWithKill = 10,
            RoundsWithKast = 15,
            Mvps = 0,
            Abandoned = false
        };

        var playerStats = new Dictionary<ulong, MatchPlayerStats>
        {
            { badPerformer.SteamId, badPerformer },
            { opponent.SteamId, opponent }
        };

        // Act
        var changes = await _ratingService.ProcessMatchEndAsync(
            Guid.NewGuid().ToString(), "de_dust2", CsTeam.CounterTerrorist, 13, 7, playerStats);

        // Assert
        var change = changes.Single(c => c.SteamId == "76561198000000400");
        Assert.True(change.Won);
        Assert.True(change.TotalChange >= _config.MinRatingChangeMagnitude,
            $"Vitória não pode valer menos que o piso mínimo configurado ({_config.MinRatingChangeMagnitude}), mas TotalChange foi {change.TotalChange}.");
        Assert.Equal(change.TotalChange, change.BaseChange + change.PerformanceSwing);
    }

    [Fact]
    public async Task ProcessMatchEndAsync_GuaranteesNegativeChange_WhenHeavyUnderdogLosesWithGreatPerformance()
    {
        // Arrange: cenário simétrico — time T muito underdog (1000) perde para o
        // CT (1400), mas o jogador do T tem uma performance excelente o bastante
        // para, sem a garantia, terminar a partida com pontos positivos apesar
        // de ter perdido.
        _db.Players["76561198000000500"] = new PlayerData
        {
            SteamId = "76561198000000500", Name = "Favorite",
            Rating = 1400, Matches = 10, Wins = 5, Losses = 5
        };
        _db.Players["76561198000000501"] = new PlayerData
        {
            SteamId = "76561198000000501", Name = "UnderdogGreatPerformance",
            Rating = 1000, Matches = 10, Wins = 5, Losses = 5
        };

        var favorite = new MatchPlayerStats
        {
            SteamId = 76561198000000500,
            PlayerName = "Favorite",
            Team = CsTeam.CounterTerrorist,
            Kills = 15,
            Deaths = 15,
            Assists = 0,
            Damage = 1500,
            RoundsPlayed = 20,
            RoundsSurvived = 5,
            RoundsWithKill = 10,
            RoundsWithKast = 15,
            Mvps = 0,
            Abandoned = false
        };
        var greatPerformer = new MatchPlayerStats
        {
            SteamId = 76561198000000501,
            PlayerName = "UnderdogGreatPerformance",
            Team = CsTeam.Terrorist,
            Kills = 30,
            Deaths = 10,
            Assists = 0,
            Damage = 3000,
            RoundsPlayed = 20,
            RoundsSurvived = 10,
            RoundsWithKill = 20,
            RoundsWithKast = 20,
            Mvps = 6,
            Abandoned = false
        };

        var playerStats = new Dictionary<ulong, MatchPlayerStats>
        {
            { favorite.SteamId, favorite },
            { greatPerformer.SteamId, greatPerformer }
        };

        // Act
        var changes = await _ratingService.ProcessMatchEndAsync(
            Guid.NewGuid().ToString(), "de_dust2", CsTeam.CounterTerrorist, 13, 7, playerStats);

        // Assert
        var change = changes.Single(c => c.SteamId == "76561198000000501");
        Assert.False(change.Won);
        Assert.True(change.TotalChange <= -_config.MinRatingChangeMagnitude,
            $"Derrota não pode valer menos (em módulo) que o piso mínimo configurado ({_config.MinRatingChangeMagnitude}), mas TotalChange foi {change.TotalChange}.");
        Assert.Equal(change.TotalChange, change.BaseChange + change.PerformanceSwing);
    }

    [Fact]
    public async Task ProcessMatchEndAsync_DoesNotOverrideAbandonPenalty_WhenAbandonerIsOnWinningTeam()
    {
        // Arrange: mesmo cenário de favorito extremo do teste acima, mas o
        // jogador com performance ruim abandonou a partida. A garantia de piso
        // mínimo não deve neutralizar a AbandonPenalty.
        _db.Players["76561198000000600"] = new PlayerData
        {
            SteamId = "76561198000000600", Name = "AbandonerOnWinningTeam",
            Rating = 1400, Matches = 10, Wins = 5, Losses = 5
        };
        _db.Players["76561198000000601"] = new PlayerData
        {
            SteamId = "76561198000000601", Name = "Underdog",
            Rating = 1000, Matches = 10, Wins = 5, Losses = 5
        };

        var abandoner = new MatchPlayerStats
        {
            SteamId = 76561198000000600,
            PlayerName = "AbandonerOnWinningTeam",
            Team = CsTeam.CounterTerrorist,
            Kills = 6,
            Deaths = 20,
            Assists = 0,
            Damage = 800,
            RoundsPlayed = 20,
            RoundsSurvived = 0,
            RoundsWithKill = 5,
            RoundsWithKast = 6,
            Mvps = 0,
            Abandoned = true
        };
        var opponent = new MatchPlayerStats
        {
            SteamId = 76561198000000601,
            PlayerName = "Underdog",
            Team = CsTeam.Terrorist,
            Kills = 15,
            Deaths = 15,
            Assists = 0,
            Damage = 1500,
            RoundsPlayed = 20,
            RoundsSurvived = 5,
            RoundsWithKill = 10,
            RoundsWithKast = 15,
            Mvps = 0,
            Abandoned = false
        };

        var playerStats = new Dictionary<ulong, MatchPlayerStats>
        {
            { abandoner.SteamId, abandoner },
            { opponent.SteamId, opponent }
        };

        // Act
        var changes = await _ratingService.ProcessMatchEndAsync(
            Guid.NewGuid().ToString(), "de_dust2", CsTeam.CounterTerrorist, 13, 7, playerStats);

        // Assert: mesmo tendo vencido, o abandono não é elevado ao piso mínimo
        var change = changes.Single(c => c.SteamId == "76561198000000600");
        Assert.True(change.Won);
        Assert.True(change.TotalChange < 0,
            "AbandonPenalty deve continuar valendo por completo mesmo em vitória do time.");
    }
}
