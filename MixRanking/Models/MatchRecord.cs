namespace MixRanking.Models;

/// <summary>Registro de uma partida finalizada no banco de dados.</summary>
public class MatchRecord
{
    /// <summary>ID auto-incremento.</summary>
    public long Id { get; set; }

    /// <summary>GUID único da partida.</summary>
    public string MatchGuid { get; set; } = string.Empty;

    /// <summary>Nome do mapa jogado.</summary>
    public string Map { get; set; } = string.Empty;

    /// <summary>Time vencedor (2 = Terrorist, 3 = CT).</summary>
    public int WinnerTeam { get; set; }

    /// <summary>Pontuação do time CT.</summary>
    public int CtScore { get; set; }

    /// <summary>Pontuação do time T.</summary>
    public int TScore { get; set; }

    /// <summary>Data/hora do término da partida.</summary>
    public DateTime FinishedAt { get; set; } = DateTime.UtcNow;
}
