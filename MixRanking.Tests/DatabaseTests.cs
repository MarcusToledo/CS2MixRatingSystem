using Microsoft.Data.Sqlite;
using MixRanking.Database;
using MixRanking.Models;
using CounterStrikeSharp.API.Modules.Utils;
using Xunit;

namespace MixRanking.Tests;

public class DatabaseTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private const string ConnectionString = "Data Source=InMemoryDb;Mode=Memory;Cache=Shared";
    private readonly DatabaseService _db;

    public DatabaseTests()
    {
        // Open a shared connection to keep the in-memory cache alive across DatabaseService opens/closes
        _keepAliveConnection = new SqliteConnection(ConnectionString);
        _keepAliveConnection.Open();
        _db = new DatabaseService("InMemoryDb;Mode=Memory;Cache=Shared");
    }

    public void Dispose()
    {
        _keepAliveConnection.Close();
        _keepAliveConnection.Dispose();
    }

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

    [Fact]
    public async Task WriteMatchEndResultAsync_SavesSuccessfullyInSingleTransaction()
    {
        await _db.InitializeAsync();

        // Arrange
        var match = new MatchRecord
        {
            MatchGuid = Guid.NewGuid().ToString(),
            Map = "de_mirage",
            WinnerTeam = 3, // CT
            CtScore = 13,
            TScore = 8,
            FinishedAt = DateTime.UtcNow
        };

        var stats = new MatchPlayerStats
        {
            SteamId = 76561198000000001,
            PlayerName = "TestPlayer1",
            Team = CsTeam.CounterTerrorist,
            Kills = 20,
            Deaths = 10,
            Assists = 5,
            Damage = 2200,
            RoundsPlayed = 21,
            RoundsSurvived = 11,
            RoundsWithKill = 12,
            RoundsWithKast = 21,
            Mvps = 3,
            OpeningKills = 2,
            OpeningDeaths = 1,
            TradeKills = 3,
            FlashAssists = 1,
            Abandoned = false
        };

        var playerData = new PlayerData
        {
            SteamId = "76561198000000001",
            Name = "TestPlayer1",
            Rating = 1000,
            Matches = 0
        };

        var ratingChange = new RatingChange
        {
            SteamId = "76561198000000001",
            OldRating = 1000,
            BaseChange = 25,
            PerformanceSwing = 5,
            TotalChange = 30,
            NewRating = 1030,
            PlayerName = "TestPlayer1",
            Won = true,
            KFactorUsed = 100
        };

        var update = new MatchPlayerUpdate
        {
            Stats = stats,
            PlayerData = playerData,
            RatingChange = ratingChange,
            NewRating = 1030,
            Won = true
        };

        // Act
        int activeSeasonId = await _db.GetActiveSeasonIdAsync();
        await _db.WriteMatchEndResultAsync(match, new List<MatchPlayerUpdate> { update }, 1000, activeSeasonId);

        // Assert
        var savedPlayer = await _db.GetPlayerAsync("76561198000000001");
        Assert.NotNull(savedPlayer);
        Assert.Equal("TestPlayer1", savedPlayer.Name);
        Assert.Equal(1030, savedPlayer.Rating);
        Assert.Equal(1, savedPlayer.Matches);
        Assert.Equal(1, savedPlayer.Wins);
        Assert.Equal(0, savedPlayer.Losses);
        Assert.Equal(20, savedPlayer.Kills);

        // Check history
        var history = await _db.GetRatingHistoryAsync("76561198000000001");
        Assert.Single(history);
        Assert.Equal(1000, history[0].OldRating);
        Assert.Equal(30, history[0].TotalChange);
        Assert.Equal(1030, history[0].NewRating);
        Assert.Equal(100, history[0].KFactorUsed);

        // Check stats
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kills, rounds_played, kast_percent, clutches_won, headshots FROM match_player_stats WHERE steamid = '76561198000000001';";
        using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(20, reader.GetInt32(0));
            Assert.Equal(21, reader.GetInt32(1));
            // KAST = RoundsWithKast/RoundsPlayed, capped at 1.0 (every round contributed here)
            Assert.Equal(1.0, reader.GetDouble(2));
            Assert.Equal(0, reader.GetInt32(3));
            Assert.Equal(0, reader.GetInt32(4));
        }

        // Check season_ratings
        command.CommandText = "SELECT rating, matches FROM season_ratings WHERE steamid = '76561198000000001' AND season_id = 1;";
        using (var readerSeason = await command.ExecuteReaderAsync())
        {
            Assert.True(await readerSeason.ReadAsync());
            Assert.Equal(1030, readerSeason.GetInt32(0));
            Assert.Equal(1, readerSeason.GetInt32(1));
        }
    }

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

    [Fact]
    public async Task SetPlayerRatingWithAuditAsync_SavesAuditLogSuccessfully()
    {
        await _db.InitializeAsync();
        // Create player
        await _db.GetOrCreatePlayerAsync("76561198000000002", "AdminTarget", 1000);

        // Act
        await _db.SetPlayerRatingWithAuditAsync("76561198000000002", 1250, "76561198000000000", "AdminName", "Forced update");

        // Assert
        var updated = await _db.GetPlayerAsync("76561198000000002");
        Assert.Equal(1250, updated!.Rating);

        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT action, admin_steamid, admin_name, target_steamid, old_value, new_value, reason FROM admin_audit_log ORDER BY id DESC LIMIT 1;";
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("SET", reader.GetString(0));
        Assert.Equal("76561198000000000", reader.GetString(1));
        Assert.Equal("AdminName", reader.GetString(2));
        Assert.Equal("76561198000000002", reader.GetString(3));
        Assert.Equal(1000, reader.GetInt32(4));
        Assert.Equal(1250, reader.GetInt32(5));
        Assert.Equal("Forced update", reader.GetString(6));
    }

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

    [Fact]
    public async Task ClearWebSyncQueueEntriesAsync_IsNoOpOnEmptyList()
    {
        await _db.InitializeAsync();

        await _db.UpsertWebSyncQueueAsync(new PlayerData { SteamId = "76561198000000023", Name = "C", CreatedAt = DateTime.UtcNow });

        // Act - clear with empty list should not remove anything
        await _db.ClearWebSyncQueueEntriesAsync(new List<string>());

        // Assert - the entry should still be there
        var remaining = await _db.GetPendingWebSyncEntriesAsync(limit: 10);
        Assert.Single(remaining);
        Assert.Equal("76561198000000023", remaining[0].SteamId);
    }

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
}
