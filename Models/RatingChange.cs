namespace MixRanking.Models;

/// <summary>Registro de mudança de rating de um jogador em uma partida.</summary>
public class RatingChange
{
    /// <summary>ID auto-incremento.</summary>
    public long Id { get; set; }

    /// <summary>ID da partida associada.</summary>
    public long MatchId { get; set; }

    /// <summary>SteamID64 do jogador.</summary>
    public required string SteamId { get; set; }

    /// <summary>Rating antes da partida.</summary>
    public int OldRating { get; set; }

    /// <summary>Mudança base do Elo (resultado da partida).</summary>
    public int BaseChange { get; set; }

    /// <summary>Swing de performance individual.</summary>
    public int PerformanceSwing { get; set; }

    /// <summary>Mudança total (base + swing).</summary>
    public int TotalChange { get; set; }

    /// <summary>Rating após a partida.</summary>
    public int NewRating { get; set; }

    /// <summary>K-factor utilizado na partida.</summary>
    public int? KFactorUsed { get; set; }

    /// <summary>Nome do jogador (para exibição).</summary>
    public string PlayerName { get; set; } = string.Empty;

    /// <summary>Se o jogador venceu.</summary>
    public bool Won { get; set; }
}
