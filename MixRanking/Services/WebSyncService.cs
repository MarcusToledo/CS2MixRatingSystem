using Microsoft.Extensions.Logging;
using MixRanking.Database;
using MixRanking.Models;

namespace MixRanking.Services;

/// <summary>
/// Orquestra o sync outbound de rating/nível com a plataforma web: dirty-tracking local
/// e drenagem em lote via HTTP, sem bloquear o fluxo principal do plugin.
/// </summary>
public class WebSyncService
{
    private const int BatchLimit = 100;

    private readonly DatabaseService _db;
    private readonly IWebSyncClient _client;
    private readonly ILogger _logger;

    public WebSyncService(DatabaseService db, IWebSyncClient client, ILogger logger)
    {
        _db = db;
        _client = client;
        _logger = logger;
    }

    /// <summary>Marca o estado atual de um jogador como pendente de sync. Nunca lança exceção.</summary>
    public async Task MarkDirtyAsync(PlayerData player)
    {
        try
        {
            await _db.UpsertWebSyncQueueAsync(player);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MixRanking] Failed to mark player {SteamId} for web sync.", player.SteamId);
        }
    }

    /// <summary>Drena a fila pendente (ou sinaliza wipe) para a plataforma web. Nunca lança exceção.</summary>
    public async Task DrainPendingAsync()
    {
        try
        {
            if (await _db.IsWipePendingAsync())
            {
                bool wipeSent = await _client.SendWipeAsync();
                if (wipeSent)
                {
                    await _db.ClearWipePendingAsync();
                }
                else
                {
                    _logger.LogWarning("[MixRanking] Web sync wipe signal failed, will retry next tick.");
                }
                return;
            }

            var pending = await _db.GetPendingWebSyncEntriesAsync(BatchLimit);
            if (pending.Count == 0) return;

            bool success = await _client.SendPlayersAsync(pending);
            if (success)
            {
                await _db.ClearWebSyncQueueEntriesAsync(pending.Select(p => p.SteamId).ToList());
            }
            else
            {
                _logger.LogWarning("[MixRanking] Web sync failed, {Count} player(s) remain pending.", pending.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MixRanking] Unexpected error during web sync drain.");
        }
    }
}
