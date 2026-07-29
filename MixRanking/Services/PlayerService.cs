using MixRanking.Database;
using MixRanking.Models;

namespace MixRanking.Services;

/// <summary>Interface entre o plugin e o banco para operações de jogador.</summary>
public class PlayerService
{
    private readonly DatabaseService _db;
    private readonly int _initialRating;

    public PlayerService(DatabaseService db, int initialRating)
    {
        _db = db;
        _initialRating = initialRating;
    }

    /// <summary>Obtém ou cria um jogador pelo SteamID64.</summary>
    public Task<PlayerData> GetOrCreatePlayerAsync(string steamId, string name)
        => _db.GetOrCreatePlayerAsync(steamId, name, _initialRating);

    /// <summary>Obtém um jogador pelo SteamID64 (pode retornar null).</summary>
    public Task<PlayerData?> GetPlayerAsync(string steamId)
        => _db.GetPlayerAsync(steamId);

    /// <summary>Retorna os top N jogadores.</summary>
    public Task<List<PlayerData>> GetTopPlayersAsync(int count = 10)
        => _db.GetTopPlayersAsync(count);

    /// <summary>Retorna a posição no ranking do jogador.</summary>
    public Task<int> GetRankPositionAsync(string steamId)
        => _db.GetPlayerRankPositionAsync(steamId);

    /// <summary>Retorna o total de jogadores rankeados.</summary>
    public Task<int> GetTotalRankedPlayersAsync()
        => _db.GetTotalRankedPlayersAsync();

    /// <summary>Retorna o histórico de rating de um jogador.</summary>
    public Task<List<RatingChange>> GetRatingHistoryAsync(string steamId, int count = 10)
        => _db.GetRatingHistoryAsync(steamId, count);

    /// <summary>Retorna o último rating change de um jogador.</summary>
    public Task<RatingChange?> GetLastRatingChangeAsync(string steamId)
        => _db.GetLastRatingChangeAsync(steamId);

    /// <summary>Obtém informações de uma partida.</summary>
    public Task<MatchRecord?> GetMatchByIdAsync(long matchId)
        => _db.GetMatchByIdAsync(matchId);
}
