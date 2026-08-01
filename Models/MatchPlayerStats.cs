using CounterStrikeSharp.API.Modules.Utils;

namespace MixRanking.Models;

/// <summary>Estatísticas temporárias de um jogador durante uma partida ativa.</summary>
public class MatchPlayerStats
{
    /// <summary>SteamID64 do jogador.</summary>
    public required ulong SteamId { get; init; }

    /// <summary>Nome do jogador.</summary>
    public string PlayerName { get; set; } = string.Empty;

    /// <summary>Time atual do jogador.</summary>
    public CsTeam Team { get; set; } = CsTeam.None;

    /// <summary>Kills na partida.</summary>
    public int Kills { get; set; }

    /// <summary>Deaths na partida.</summary>
    public int Deaths { get; set; }

    /// <summary>Assistências na partida.</summary>
    public int Assists { get; set; }

    /// <summary>Dano total infligido.</summary>
    public int Damage { get; set; }

    /// <summary>Rounds jogados.</summary>
    public int RoundsPlayed { get; set; }

    /// <summary>Rounds sobrevividos.</summary>
    public int RoundsSurvived { get; set; }

    /// <summary>Rounds em que o jogador fez pelo menos um kill.</summary>
    public int RoundsWithKill { get; set; }

    /// <summary>MVPs recebidos.</summary>
    public int Mvps { get; set; }

    /// <summary>Opening kills (primeiro kill do round).</summary>
    public int OpeningKills { get; set; }

    /// <summary>Opening deaths (primeira morte do round).</summary>
    public int OpeningDeaths { get; set; }

    /// <summary>Clutches ganhos.</summary>
    public int ClutchesWon { get; set; }

    /// <summary>Trade kills (kill em menos de 5s após teammate morrer).</summary>
    public int TradeKills { get; set; }

    /// <summary>Flash assists.</summary>
    public int FlashAssists { get; set; }

    /// <summary>Rounds em que o jogador teve pelo menos um evento de KAST (Kill, Assist, Survived ou Traded), sem contagem duplicada.</summary>
    public int RoundsWithKast { get; set; }

    /// <summary>Se o jogador estava vivo no início do round atual.</summary>
    public bool WasAliveAtRoundStart { get; set; }

    /// <summary>Se o jogador fez kill neste round.</summary>
    public bool GotKillThisRound { get; set; }

    /// <summary>Se o jogador deu assistência neste round.</summary>
    public bool GotAssistThisRound { get; set; }

    /// <summary>Se a morte do jogador neste round foi vingada por um teammate dentro da janela de trade.</summary>
    public bool WasTradedThisRound { get; set; }

    /// <summary>Se o jogador abandonou a partida.</summary>
    public bool Abandoned { get; set; }

    /// <summary>ADR (Average Damage per Round).</summary>
    public double Adr => RoundsPlayed > 0 ? (double)Damage / RoundsPlayed : 0;

    /// <summary>K/D Ratio.</summary>
    public double KdRatio => Deaths > 0 ? (double)Kills / Deaths : Kills;

    /// <summary>Reseta flags por round.</summary>
    public void ResetRoundFlags()
    {
        WasAliveAtRoundStart = true;
        GotKillThisRound = false;
        GotAssistThisRound = false;
        WasTradedThisRound = false;
    }
}
