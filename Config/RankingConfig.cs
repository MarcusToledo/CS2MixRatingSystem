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

    /// <summary>
    /// Magnitude mínima garantida da mudança total de rating por resultado de partida.
    /// Uma vitória nunca resulta em ganho menor que este valor; uma derrota nunca resulta
    /// em perda menor (em módulo) que este valor. Jogadores marcados como Abandoned ficam
    /// fora desta garantia (a AbandonPenalty deve valer por completo). 0 desativa a garantia.
    /// </summary>
    [JsonPropertyName("MinRatingChangeMagnitude")]
    public int MinRatingChangeMagnitude { get; set; } = 3;

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

    /// <summary>URL base do projeto Supabase dedicado ao plugin (ex: https://xxxx.supabase.co).</summary>
    [JsonPropertyName("SupabaseUrl")]
    public string SupabaseUrl { get; set; } = "";

    /// <summary>Service role key do projeto Supabase, enviada nos headers apikey/Authorization.</summary>
    [JsonPropertyName("SupabaseServiceKey")]
    public string SupabaseServiceKey { get; set; } = "";

    /// <summary>Número de partidas de placement.</summary>
    [JsonPropertyName("PlacementMatchCount")]
    public int PlacementMatchCount { get; set; } = 10;

    /// <summary>Multiplicador do fator K durante o placement.</summary>
    [JsonPropertyName("PlacementKFactorMultiplier")]
    public double PlacementKFactorMultiplier { get; set; } = 2.0;

    /// <summary>Prefixo utilizado nas mensagens do chat.</summary>
    [JsonPropertyName("ChatPrefix")]
    public string ChatPrefix { get; set; } = "GurizadaMix";

    /// <summary>Nome do modo (reportado pelo GameModeManager) que deve ser ranqueado. Outros modos (ex: Retake) são ignorados.</summary>
    [JsonPropertyName("RankedModeName")]
    public string RankedModeName { get; set; } = "Competitive";

    /// <summary>Habilita o sync outbound de rating/nível com a plataforma web.</summary>
    [JsonPropertyName("WebSyncEnabled")]
    public bool WebSyncEnabled { get; set; } = false;

    /// <summary>URL HTTPS de destino do sync outbound.</summary>
    [JsonPropertyName("WebSyncUrl")]
    public string WebSyncUrl { get; set; } = "";

    /// <summary>Chave de API enviada no header Authorization do sync outbound.</summary>
    [JsonPropertyName("WebSyncApiKey")]
    public string WebSyncApiKey { get; set; } = "";

    /// <summary>Intervalo em segundos entre drenagens da fila de sync outbound.</summary>
    [JsonPropertyName("WebSyncIntervalSeconds")]
    public int WebSyncIntervalSeconds { get; set; } = 30;
}
