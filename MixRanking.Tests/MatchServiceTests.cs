using Microsoft.Extensions.Logging.Abstractions;
using MixRanking.Config;
using MixRanking.Match;
using MixRanking.Services;
using Xunit;

namespace MixRanking.Tests;

public class MatchServiceTests
{
    private static MatchService CreateMatchService()
    {
        var db = new FakeDatabaseService();
        var config = new RankingConfig();
        var webSyncService = new WebSyncService(db, new FakeWebSyncClient(), NullLogger.Instance);
        var ratingService = new RatingService(db, config, webSyncService, NullLogger.Instance);
        var statistics = new StatisticsService();

        return new MatchService(statistics, ratingService, config, NullLogger.Instance);
    }

    [Fact]
    public void MarkEnded_SetsIsLiveToFalse_Synchronously()
    {
        // Reproduz a correção da race condition: OnPlayerDisconnect só deve marcar
        // abandono enquanto IsLive == true. MarkEnded() precisa ser chamável de forma
        // síncrona (thread principal) assim que o resultado da partida é conhecido,
        // ANTES de qualquer processamento assíncrono — para que desconexões que
        // ocorrem logo após o fim da partida (comportamento normal do jogador) não
        // sejam mais vistas como "partida ainda live" e erroneamente tratadas como abandono.
        var matchService = CreateMatchService();
        matchService.StartMatch();
        matchService.SetLive(ctCount: 5, tCount: 5);

        Assert.True(matchService.IsLive);

        matchService.MarkEnded();

        Assert.False(matchService.IsLive);
    }

    [Fact]
    public void MarkEnded_DoesNotThrow_WhenCalledBeforeMatchIsLive()
    {
        var matchService = CreateMatchService();
        matchService.StartMatch();

        matchService.MarkEnded();

        Assert.False(matchService.IsLive);
    }
}
