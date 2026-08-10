using CounterStrikeSharp.API.Modules.Utils;
using MixRanking.Models;

namespace MixRanking.Services;

/// <summary>
/// Acumula estatísticas de cada jogador durante uma partida ativa.
/// O objeto é descartado ao fim da partida.
/// </summary>
public class StatisticsService
{
    /// <summary>Estatísticas temporárias por SteamID64.</summary>
    private readonly Dictionary<ulong, MatchPlayerStats> _playerStats = new();

    /// <summary>Tracking de mortes recentes para detecção de trade kills (timestamp, steamId do morto, time do morto).</summary>
    private readonly List<(DateTime Time, ulong VictimSteamId, CsTeam VictimTeam)> _recentDeaths = new();

    /// <summary>Se o primeiro kill do round já ocorreu.</summary>
    private bool _firstKillThisRound;

    /// <summary>Retorna as estatísticas de todos os jogadores.</summary>
    public Dictionary<ulong, MatchPlayerStats> GetAllPlayerStats() => _playerStats;

    /// <summary>Obtém ou cria as estatísticas de um jogador.</summary>
    public MatchPlayerStats GetOrCreateStats(ulong steamId, string playerName, CsTeam team)
    {
        if (!_playerStats.TryGetValue(steamId, out var stats))
        {
            stats = new MatchPlayerStats
            {
                SteamId = steamId,
                PlayerName = playerName,
                Team = team
            };
            _playerStats[steamId] = stats;
        }
        // Always update team (could swap sides)
        stats.Team = team;
        stats.PlayerName = playerName;
        return stats;
    }

    /// <summary>Registra um kill.</summary>
    public void RecordKill(ulong attackerSteamId, ulong victimSteamId, CsTeam victimTeam, bool assistedFlash)
    {
        if (_playerStats.TryGetValue(attackerSteamId, out var attackerStats))
        {
            attackerStats.Kills++;
            attackerStats.GotKillThisRound = true;

            // Opening kill detection
            if (!_firstKillThisRound)
            {
                attackerStats.OpeningKills++;
                _firstKillThisRound = true;
            }

            // Trade kill detection (kill within 5 seconds of a teammate dying)
            var now = DateTime.UtcNow;
            for (int i = _recentDeaths.Count - 1; i >= 0; i--)
            {
                var death = _recentDeaths[i];
                if ((now - death.Time).TotalSeconds > 5.0)
                    break;

                // If the victim of the recent death was on the same team as the attacker,
                // this is a trade kill (attacker traded for their fallen teammate).
                // The teammate who died gets KAST credit for being traded — the avenger
                // is already credited via GotKillThisRound, so it isn't counted twice.
                if (death.VictimTeam == attackerStats.Team && death.VictimSteamId != attackerSteamId)
                {
                    attackerStats.TradeKills++;
                    if (_playerStats.TryGetValue(death.VictimSteamId, out var tradedStats))
                    {
                        tradedStats.WasTradedThisRound = true;
                    }
                    break;
                }
            }
        }

        // Record victim death
        if (_playerStats.TryGetValue(victimSteamId, out var victimStats))
        {
            victimStats.Deaths++;

            // Opening death detection
            if (!_firstKillThisRound || _playerStats.Values.Sum(p => p.Deaths) == 1)
            {
                // The first person to die in the round gets an opening death
                // (only if this is the first death)
                victimStats.OpeningDeaths++;
            }
        }

        // Track for trade kill detection — only real enemy kills create trade
        // eligibility. A death with no real attacker (suicide, fall damage, world
        // kill, attackerSteamId == 0) must not let a teammate's later kill be
        // misread as a trade, nor grant the victim a phantom "traded" KAST credit.
        if (attackerSteamId != 0)
        {
            _recentDeaths.Add((DateTime.UtcNow, victimSteamId, victimTeam));
        }
    }

    /// <summary>Registra uma assistência.</summary>
    public void RecordAssist(ulong assistSteamId, bool flashAssist)
    {
        if (_playerStats.TryGetValue(assistSteamId, out var stats))
        {
            stats.Assists++;
            stats.GotAssistThisRound = true;
            if (flashAssist)
                stats.FlashAssists++;
        }
    }

    /// <summary>Registra dano infligido.</summary>
    public void RecordDamage(ulong attackerSteamId, int damage)
    {
        if (_playerStats.TryGetValue(attackerSteamId, out var stats))
        {
            // Cap damage at 100 per hit (prevent overkill inflation)
            stats.Damage += Math.Min(damage, 100);
        }
    }

    /// <summary>Registra um MVP.</summary>
    public void RecordMvp(ulong steamId)
    {
        if (_playerStats.TryGetValue(steamId, out var stats))
        {
            stats.Mvps++;
        }
    }

    /// <summary>Chamado no início de cada round.</summary>
    public void OnRoundStart()
    {
        _firstKillThisRound = false;
        _recentDeaths.Clear();

        foreach (var stats in _playerStats.Values)
        {
            stats.RoundsPlayed++;
            stats.ResetRoundFlags();
        }
    }

    /// <summary>Chamado no fim de cada round. Marca sobreviventes.</summary>
    public void OnRoundEnd(IEnumerable<ulong> aliveSteamIds)
    {
        var aliveSet = new HashSet<ulong>(aliveSteamIds);

        foreach (var stats in _playerStats.Values)
        {
            bool survived = aliveSet.Contains(stats.SteamId);
            if (survived)
            {
                stats.RoundsSurvived++;
            }

            if (stats.GotKillThisRound)
            {
                stats.RoundsWithKill++;
            }

            // União, não soma: um round só conta uma vez para KAST mesmo que o
            // jogador tenha matado, sobrevivido, assistido e sido vingado ao mesmo tempo.
            if (stats.GotKillThisRound || survived || stats.GotAssistThisRound || stats.WasTradedThisRound)
            {
                stats.RoundsWithKast++;
            }
        }
    }

    /// <summary>Marca um jogador como tendo abandonado.</summary>
    public void MarkAbandoned(ulong steamId)
    {
        if (_playerStats.TryGetValue(steamId, out var stats))
        {
            stats.Abandoned = true;
        }
    }

    /// <summary>
    /// Reverte a marca de abandono de um jogador que reconectou antes do fim da partida.
    /// Sem isso, um disconnect momentâneo (queda de rede, reload de plugin) deixa a flag
    /// travada em true mesmo que o jogador volte e jogue o resto da partida normalmente.
    /// </summary>
    /// <returns>True se havia uma marca de abandono para reverter.</returns>
    public bool ClearAbandoned(ulong steamId)
    {
        if (_playerStats.TryGetValue(steamId, out var stats) && stats.Abandoned)
        {
            stats.Abandoned = false;
            return true;
        }
        return false;
    }

    /// <summary>Limpa todas as estatísticas (início de nova partida).</summary>
    public void Reset()
    {
        _playerStats.Clear();
        _recentDeaths.Clear();
        _firstKillThisRound = false;
    }
}
