using MixRanking.Models;

namespace MixRanking.Services;

/// <summary>Cliente do sync outbound de rating/nível com a plataforma web.</summary>
public interface IWebSyncClient
{
    /// <summary>Envia um lote de jogadores. Retorna true se o lote inteiro foi aceito (200).</summary>
    Task<bool> SendPlayersAsync(IReadOnlyList<PlayerData> players);

    /// <summary>Envia o sinal de wipe. Retorna true se aceito (200).</summary>
    Task<bool> SendWipeAsync();
}
