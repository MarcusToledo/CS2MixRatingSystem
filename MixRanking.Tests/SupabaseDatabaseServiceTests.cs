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
}
