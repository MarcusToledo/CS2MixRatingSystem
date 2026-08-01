using System.Text.Json.Serialization;

namespace MixRanking.Models;

/// <summary>Payload de um jogador no sync outbound para a plataforma web.</summary>
public class WebSyncPlayerPayload
{
    [JsonPropertyName("steamid")]
    public required string SteamId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("rating")]
    public int Rating { get; set; }

    [JsonPropertyName("matches")]
    public int Matches { get; set; }

    [JsonPropertyName("wins")]
    public int Wins { get; set; }

    [JsonPropertyName("losses")]
    public int Losses { get; set; }

    [JsonPropertyName("kills")]
    public int Kills { get; set; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; set; }

    [JsonPropertyName("assists")]
    public int Assists { get; set; }

    [JsonPropertyName("damage")]
    public long Damage { get; set; }

    [JsonPropertyName("mvps")]
    public int Mvps { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>Lote de jogadores enviado no sync outbound.</summary>
public class WebSyncBatchPayload
{
    [JsonPropertyName("players")]
    public required List<WebSyncPlayerPayload> Players { get; set; }
}

/// <summary>Sinal isolado enviado após um !rating_wipe.</summary>
public class WebSyncWipePayload
{
    [JsonPropertyName("wipe")]
    public bool Wipe { get; set; } = true;
}
