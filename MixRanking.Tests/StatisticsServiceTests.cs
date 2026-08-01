using CounterStrikeSharp.API.Modules.Utils;
using MixRanking.Services;
using Xunit;

namespace MixRanking.Tests;

public class StatisticsServiceTests
{
    private const ulong AttackerSteamId = 76561198000000010;
    private const ulong VictimSteamId = 76561198000000020;
    private const ulong TraderSteamId = 76561198000000030;

    [Fact]
    public void OnRoundEnd_DoesNotDoubleCount_WhenPlayerBothKillsAndSurvives()
    {
        var service = new StatisticsService();
        service.GetOrCreateStats(AttackerSteamId, "Attacker", CsTeam.CounterTerrorist);
        service.GetOrCreateStats(VictimSteamId, "Victim", CsTeam.Terrorist);

        service.OnRoundStart();
        service.RecordKill(AttackerSteamId, VictimSteamId, CsTeam.Terrorist, false);
        service.OnRoundEnd(new[] { AttackerSteamId });

        var stats = service.GetAllPlayerStats()[AttackerSteamId];

        Assert.Equal(1, stats.RoundsWithKill);
        Assert.Equal(1, stats.RoundsSurvived);
        Assert.Equal(1, stats.RoundsWithKast);
    }

    [Fact]
    public void OnRoundEnd_CreditsAssistTowardKast()
    {
        var service = new StatisticsService();
        ulong killerSteamId = 76561198000000040;
        service.GetOrCreateStats(killerSteamId, "Killer", CsTeam.CounterTerrorist);
        service.GetOrCreateStats(AttackerSteamId, "Assister", CsTeam.CounterTerrorist);
        service.GetOrCreateStats(VictimSteamId, "Victim", CsTeam.Terrorist);

        service.OnRoundStart();
        service.RecordAssist(AttackerSteamId, false);
        service.RecordKill(killerSteamId, VictimSteamId, CsTeam.Terrorist, false);
        service.OnRoundEnd(new[] { killerSteamId });

        var stats = service.GetAllPlayerStats()[AttackerSteamId];

        Assert.Equal(1, stats.RoundsWithKast);
    }

    [Fact]
    public void RecordKill_CreditsTradedVictim_NotJustTheAvenger()
    {
        var service = new StatisticsService();
        service.GetOrCreateStats(AttackerSteamId, "Attacker", CsTeam.Terrorist);
        service.GetOrCreateStats(VictimSteamId, "Victim", CsTeam.CounterTerrorist);
        service.GetOrCreateStats(TraderSteamId, "Trader", CsTeam.CounterTerrorist);

        service.OnRoundStart();

        // Attacker mata Victim primeiro
        service.RecordKill(AttackerSteamId, VictimSteamId, CsTeam.CounterTerrorist, false);
        // Trader (teammate de Victim) vinga Victim matando Attacker
        service.RecordKill(TraderSteamId, AttackerSteamId, CsTeam.Terrorist, false);

        // Só Trader está vivo no fim do round
        service.OnRoundEnd(new[] { TraderSteamId });

        var victimStats = service.GetAllPlayerStats()[VictimSteamId];
        var traderStats = service.GetAllPlayerStats()[TraderSteamId];

        // Victim morreu mas foi vingado -> ainda recebe crédito de KAST
        Assert.Equal(1, victimStats.RoundsWithKast);
        // Trader recebe crédito pelo próprio kill, não por um bônus redundante de trade
        Assert.Equal(1, traderStats.RoundsWithKast);
        Assert.Equal(1, traderStats.TradeKills);
    }
}
