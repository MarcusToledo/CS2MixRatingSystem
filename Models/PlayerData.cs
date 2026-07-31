namespace MixRanking.Models;

/// <summary>Representa um jogador persistido no banco de dados.</summary>
public class PlayerData
{
    /// <summary>SteamID64 do jogador (chave primária).</summary>
    public required string SteamId { get; set; }

    /// <summary>Nome do jogador (atualizado a cada conexão).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Rating Elo atual.</summary>
    public int Rating { get; set; } = 1000;

    /// <summary>Total de partidas jogadas.</summary>
    public int Matches { get; set; }

    /// <summary>Total de vitórias.</summary>
    public int Wins { get; set; }

    /// <summary>Total de derrotas.</summary>
    public int Losses { get; set; }

    /// <summary>Total de kills acumuladas.</summary>
    public int Kills { get; set; }

    /// <summary>Total de deaths acumuladas.</summary>
    public int Deaths { get; set; }

    /// <summary>Total de assistências acumuladas.</summary>
    public int Assists { get; set; }

    /// <summary>Total de dano acumulado.</summary>
    public long Damage { get; set; }

    /// <summary>Total de MVPs acumulados.</summary>
    public int Mvps { get; set; }

    /// <summary>Data de criação do registro.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Data da última atualização.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
