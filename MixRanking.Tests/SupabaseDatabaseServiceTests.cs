using System.Net;
using System.Text;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using MixRanking.Config;
using MixRanking.Database;
using MixRanking.Models;
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
        // Return a fresh clone rather than the shared ResponseToReturn instance: production code
        // correctly disposes each HttpResponseMessage it receives (`using var response = ...`),
        // which would otherwise dispose ResponseToReturn.Content and break any test — like
        // GetOrCreatePlayerAsync — that issues more than one request against the same handler.
        // The clone copies both response-level headers and content-level headers (e.g.
        // Content-Range) from ResponseToReturn, so tests that configure those headers (see
        // GetTotalRankedPlayersAsync, which reads Content-Range) are faithfully reproduced
        // rather than silently stripped.
        var clonedResponse = new HttpResponseMessage(ResponseToReturn.StatusCode);
        foreach (var header in ResponseToReturn.Headers)
        {
            clonedResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (ResponseToReturn.Content != null)
        {
            byte[] bodyBytes = await ResponseToReturn.Content.ReadAsByteArrayAsync(cancellationToken);
            clonedResponse.Content = new ByteArrayContent(bodyBytes);
            foreach (var header in ResponseToReturn.Content.Headers)
            {
                clonedResponse.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return clonedResponse;
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
}

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
