using CounterStrikeSharp.API.Modules.Utils;
using MixRanking.Match;
using Xunit;

namespace MixRanking.Tests;

public class PlayerFiltersTests
{
    [Theory]
    [InlineData(CsTeam.CounterTerrorist, true)]
    [InlineData(CsTeam.Terrorist, true)]
    [InlineData(CsTeam.Spectator, false)]
    [InlineData(CsTeam.None, false)]
    public void IsActiveMatchPlayer_FiltersByTeam(CsTeam team, bool expected)
    {
        bool result = PlayerFilters.IsActiveMatchPlayer(isValid: true, isBot: false, isHltv: false, team: team);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsActiveMatchPlayer_ReturnsFalse_ForBots()
    {
        bool result = PlayerFilters.IsActiveMatchPlayer(isValid: true, isBot: true, isHltv: false, team: CsTeam.CounterTerrorist);

        Assert.False(result);
    }

    [Fact]
    public void IsActiveMatchPlayer_ReturnsFalse_ForInvalidController()
    {
        bool result = PlayerFilters.IsActiveMatchPlayer(isValid: false, isBot: false, isHltv: false, team: CsTeam.CounterTerrorist);

        Assert.False(result);
    }

    [Fact]
    public void IsActiveMatchPlayer_ReturnsFalse_ForHltv()
    {
        bool result = PlayerFilters.IsActiveMatchPlayer(isValid: true, isBot: false, isHltv: true, team: CsTeam.CounterTerrorist);

        Assert.False(result);
    }
}
