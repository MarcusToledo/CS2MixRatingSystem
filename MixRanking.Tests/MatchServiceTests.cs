using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using MixRanking.Config;
using MixRanking.Match;
using MixRanking.Services;
using Xunit;

namespace MixRanking.Tests;

public class MatchServiceTests
{
    private static MatchService CreateMatchService(StatisticsService? statistics = null)
    {
        var db = new FakeDatabaseService();
        var config = new RankingConfig();
        var webSyncService = new WebSyncService(db, new FakeWebSyncClient(), NullLogger.Instance);
        var ratingService = new RatingService(db, config, webSyncService, NullLogger.Instance);
        statistics ??= new StatisticsService();

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

    [Fact]
    public void SetLive_FirstCall_MarksLiveAndKeepsStats()
    {
        var statistics = new StatisticsService();
        var match = CreateMatchService(statistics);
        match.StartMatch();
        statistics.GetOrCreateStats(123, "Player", CsTeam.CounterTerrorist);
        match.OnRoundStart();

        match.SetLive(4, 4);

        Assert.True(match.IsLive);
        Assert.Equal(4, match.InitialCtCount);
        Assert.Equal(4, match.InitialTCount);
        // Stats/CurrentRound gathered during warmup, right before going live, must survive.
        Assert.Equal(1, match.CurrentRound);
        Assert.True(statistics.GetAllPlayerStats().ContainsKey(123));
    }

    [Fact]
    public void SetLive_CalledAgainWhileLive_TreatsAsRestartAndDiscardsStaleStats()
    {
        var statistics = new StatisticsService();
        var match = CreateMatchService(statistics);
        match.StartMatch();
        match.SetLive(4, 3);

        // Partida "ao vivo" acumula rounds e kills...
        match.OnRoundStart();
        match.OnRoundStart();
        statistics.GetOrCreateStats(999, "Ghost", CsTeam.Terrorist);
        statistics.RecordKill(999, 888, CsTeam.CounterTerrorist, false);

        // ...então algo externo (reload de plugin/modo) reinicia o warmup+faca no meio do
        // jogo e EventRoundAnnounceMatchStart dispara de novo com um roster diferente.
        match.SetLive(5, 5);

        Assert.True(match.IsLive);
        Assert.Equal(5, match.InitialCtCount);
        Assert.Equal(5, match.InitialTCount);
        Assert.Equal(0, match.CurrentRound);
        Assert.Empty(statistics.GetAllPlayerStats());
    }
}
