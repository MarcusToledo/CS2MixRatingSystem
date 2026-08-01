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
