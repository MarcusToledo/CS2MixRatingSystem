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

    [Fact]
    public void RecordKill_WorldOrSelfKill_DoesNotCreateTradeCredit()
    {
        var service = new StatisticsService();
        ulong teammateSteamId = 76561198000000050;
        ulong enemySteamId = 76561198000000060;
        service.GetOrCreateStats(VictimSteamId, "Victim", CsTeam.CounterTerrorist);
        service.GetOrCreateStats(teammateSteamId, "Teammate", CsTeam.CounterTerrorist);
        service.GetOrCreateStats(enemySteamId, "Enemy", CsTeam.Terrorist);

        service.OnRoundStart();

        // Victim dies to fall damage/world/self (no real attacker) — attackerSteamId 0
        service.RecordKill(0, VictimSteamId, CsTeam.CounterTerrorist, false);

        // Teammate then gets a normal enemy kill, well within the 5s trade window
        service.RecordKill(teammateSteamId, enemySteamId, CsTeam.Terrorist, false);

        service.OnRoundEnd(new[] { teammateSteamId });

        var victimStats = service.GetAllPlayerStats()[VictimSteamId];
        var teammateStats = service.GetAllPlayerStats()[teammateSteamId];

        // The self/world death had no real enemy to trade for, so it must not
        // grant the victim a phantom "traded" KAST credit...
        Assert.False(victimStats.WasTradedThisRound);
        Assert.Equal(0, victimStats.RoundsWithKast);
        // ...nor should the teammate's unrelated enemy kill be misread as a trade kill.
        Assert.Equal(0, teammateStats.TradeKills);
    }

    [Fact]
    public void OnRoundEnd_NoKastEvents_RoundsWithKastStaysZero()
    {
        var service = new StatisticsService();
        service.GetOrCreateStats(AttackerSteamId, "Bystander", CsTeam.CounterTerrorist);

        service.OnRoundStart();
        // No kill, no assist, does not survive (not in alive list), not traded.
        service.OnRoundEnd(Array.Empty<ulong>());

        var stats = service.GetAllPlayerStats()[AttackerSteamId];

        Assert.Equal(0, stats.RoundsWithKast);
    }

    [Fact]
    public void OnRoundEnd_AllFourKastConditions_DoesNotDoubleCount()
    {
        var service = new StatisticsService();
        service.GetOrCreateStats(AttackerSteamId, "AllRounder", CsTeam.CounterTerrorist);
        service.GetOrCreateStats(VictimSteamId, "Victim", CsTeam.Terrorist);

        service.OnRoundStart();

        // 1) Kill + 2) Assist for the same player in the same round.
        service.RecordAssist(AttackerSteamId, false);
        service.RecordKill(AttackerSteamId, VictimSteamId, CsTeam.Terrorist, false);

        // 4) Traded: force the flag directly, same as the player being avenged
        // after also dying earlier this round — MatchPlayerStats exposes it as a
        // public settable property, which keeps this scenario simple and explicit.
        var stats = service.GetAllPlayerStats()[AttackerSteamId];
        stats.WasTradedThisRound = true;

        // 3) Survived: included in the alive list passed to OnRoundEnd.
        service.OnRoundEnd(new[] { AttackerSteamId });

        Assert.Equal(1, stats.RoundsWithKast);
    }

    [Fact]
    public void ClearAbandoned_RevertsFlag_WhenPlayerReconnects()
    {
        var service = new StatisticsService();
        service.GetOrCreateStats(AttackerSteamId, "Player", CsTeam.CounterTerrorist);
        service.MarkAbandoned(AttackerSteamId);

        bool cleared = service.ClearAbandoned(AttackerSteamId);

        Assert.True(cleared);
        Assert.False(service.GetAllPlayerStats()[AttackerSteamId].Abandoned);
    }

    [Fact]
    public void ClearAbandoned_ReturnsFalse_WhenPlayerWasNeverAbandoned()
    {
        var service = new StatisticsService();
        service.GetOrCreateStats(AttackerSteamId, "Player", CsTeam.CounterTerrorist);

        bool cleared = service.ClearAbandoned(AttackerSteamId);

        Assert.False(cleared);
        Assert.False(service.GetAllPlayerStats()[AttackerSteamId].Abandoned);
    }

    [Fact]
    public void ClearAbandoned_ReturnsFalse_ForUnknownPlayer()
    {
        var service = new StatisticsService();

        bool cleared = service.ClearAbandoned(999999);

        Assert.False(cleared);
    }
}
