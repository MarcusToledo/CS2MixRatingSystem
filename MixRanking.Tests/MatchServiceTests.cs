using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using MixRanking.Config;
using MixRanking.Match;
using MixRanking.Services;
using Xunit;

namespace MixRanking.Tests;

public class MatchServiceTests
{
    private static MatchService CreateMatchService(StatisticsService statistics)
    {
        var db = new FakeDatabaseService();
        var config = new RankingConfig();
        var webSyncService = new WebSyncService(db, new FakeWebSyncClient(), NullLogger.Instance);
        var ratingService = new RatingService(db, config, webSyncService, NullLogger.Instance);

        return new MatchService(statistics, ratingService, config, NullLogger.Instance);
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
