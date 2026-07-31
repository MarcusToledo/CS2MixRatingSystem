using Microsoft.Data.Sqlite;
using MixRanking.Config;
using MixRanking.Database;
using MixRanking.Models;
using MixRanking.Services;
using CounterStrikeSharp.API.Modules.Utils;
using Xunit;

namespace MixRanking.Tests;

public class RatingTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private const string ConnectionString = "Data Source=InMemoryDbRating;Mode=Memory;Cache=Shared";
    private readonly DatabaseService _db;
    private readonly RankingConfig _config;
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

        _ratingService = new RatingService(_db, _config);
    }

    public void Dispose()
    {
        _keepAliveConnection.Close();
        _keepAliveConnection.Dispose();
    }

    [Fact]
    public async Task ProcessMatchEndAsync_AppliesPlacementDoubleKFactor()
    {
        await _db.InitializeAsync();

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
        // KAST = (10 + 5)/20 = 0.75 (kastScore = (75 - 50)/(90 - 50) = 25/40 = 0.625)
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
        await _db.InitializeAsync();

        // Arrange: Player already has 10 matches in DB
        // Insert player with 10 matches
        using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO players (steamid, name, rating, matches, wins, losses)
                VALUES ('76561198000000200', 'ExperiencedPlayer', 1200, 10, 5, 5);";
            await command.ExecuteNonQueryAsync();
        }

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
}
