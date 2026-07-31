using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace MixRanking.Config;

public class RankingConfig : BasePluginConfig
{
    /// <summary>Rating inicial para novos jogadores.</summary>
    [JsonPropertyName("InitialRating")]
    public int InitialRating { get; set; } = 1000;

    /// <summary>Fator K do Elo (controla magnitude das mudanças). K=50 resulta em ~25 base para vitória esperada.</summary>
    [JsonPropertyName("KFactor")]
    public int KFactor { get; set; } = 50;

    /// <summary>Swing máximo de performance (positivo ou negativo).</summary>
    [JsonPropertyName("MaxSwing")]
    public int MaxSwing { get; set; } = 7;

    /// <summary>Número mínimo de rounds para a partida ser computada.</summary>
    [JsonPropertyName("MinRounds")]
    public int MinRounds { get; set; } = 10;

    /// <summary>Número mínimo de jogadores por time para a partida ser computada.</summary>
    [JsonPropertyName("MinPlayersPerTeam")]
    public int MinPlayersPerTeam { get; set; } = 5;

    /// <summary>Penalidade de rating por abandono.</summary>
    [JsonPropertyName("AbandonPenalty")]
    public int AbandonPenalty { get; set; } = 15;

    /// <summary>Rating mínimo possível (nunca abaixo deste valor).</summary>
    [JsonPropertyName("MinRating")]
    public int MinRating { get; set; } = 100;

    /// <summary>Número de partidas de placement.</summary>
    [JsonPropertyName("PlacementMatchCount")]
    public int PlacementMatchCount { get; set; } = 10;

    /// <summary>Multiplicador do fator K durante o placement.</summary>
    [JsonPropertyName("PlacementKFactorMultiplier")]
    public double PlacementKFactorMultiplier { get; set; } = 2.0;

    /// <summary>Prefixo utilizado nas mensagens do chat.</summary>
    [JsonPropertyName("ChatPrefix")]
    public string ChatPrefix { get; set; } = "GurizadaMix";
}
