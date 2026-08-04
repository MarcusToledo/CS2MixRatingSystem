using Microsoft.Extensions.Logging.Abstractions;
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

public class WebSyncServiceTests
{
    private readonly FakeDatabaseService _db;

    public WebSyncServiceTests()
    {
        _db = new FakeDatabaseService();
    }

    [Fact]
    public async Task MarkDirtyAsync_UpsertsPlayerIntoQueue()
    {
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
