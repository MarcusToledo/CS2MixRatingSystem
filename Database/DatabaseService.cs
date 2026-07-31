using Microsoft.Data.Sqlite;
using MixRanking.Models;

namespace MixRanking.Database;

/// <summary>Serviço de persistência SQLite. Nenhuma regra de negócio aqui.</summary>
public class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
    }

    /// <summary>Cria as tabelas se não existirem.</summary>
    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS players (
                steamid TEXT PRIMARY KEY,
                name TEXT NOT NULL DEFAULT '',
                rating INTEGER NOT NULL DEFAULT 1000,
                matches INTEGER NOT NULL DEFAULT 0,
                wins INTEGER NOT NULL DEFAULT 0,
                losses INTEGER NOT NULL DEFAULT 0,
                kills INTEGER NOT NULL DEFAULT 0,
                deaths INTEGER NOT NULL DEFAULT 0,
                assists INTEGER NOT NULL DEFAULT 0,
                damage INTEGER NOT NULL DEFAULT 0,
                mvps INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS matches (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                match_guid TEXT NOT NULL,
                map TEXT NOT NULL DEFAULT '',
                winner_team INTEGER NOT NULL DEFAULT 0,
                ct_score INTEGER NOT NULL DEFAULT 0,
                t_score INTEGER NOT NULL DEFAULT 0,
                finished_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS rating_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                match_id INTEGER NOT NULL,
                steamid TEXT NOT NULL,
                old_rating INTEGER NOT NULL,
                base_change INTEGER NOT NULL,
                performance_swing INTEGER NOT NULL,
                total_change INTEGER NOT NULL,
                new_rating INTEGER NOT NULL,
                FOREIGN KEY (match_id) REFERENCES matches(id),
                FOREIGN KEY (steamid) REFERENCES players(steamid)
            );

            CREATE INDEX IF NOT EXISTS idx_rating_history_steamid ON rating_history(steamid);
            CREATE INDEX IF NOT EXISTS idx_rating_history_match ON rating_history(match_id);
            CREATE INDEX IF NOT EXISTS idx_matches_guid ON matches(match_guid);
        ";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Obtém ou cria um jogador pelo SteamID64.</summary>
    public async Task<PlayerData> GetOrCreatePlayerAsync(string steamId, string name, int initialRating)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Try to get existing player
        var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = "SELECT steamid, name, rating, matches, wins, losses, kills, deaths, assists, damage, mvps, created_at, updated_at FROM players WHERE steamid = $steamId";
        selectCmd.Parameters.AddWithValue("$steamId", steamId);

        await using var reader = await selectCmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var player = ReadPlayerFromReader(reader);
            // Update name if changed
            if (player.Name != name)
            {
                var updateNameCmd = connection.CreateCommand();
                updateNameCmd.CommandText = "UPDATE players SET name = $name, updated_at = datetime('now') WHERE steamid = $steamId";
                updateNameCmd.Parameters.AddWithValue("$name", name);
                updateNameCmd.Parameters.AddWithValue("$steamId", steamId);
                await updateNameCmd.ExecuteNonQueryAsync();
                player.Name = name;
            }
            return player;
        }

        // Create new player
        var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO players (steamid, name, rating, matches, wins, losses, kills, deaths, assists, damage, mvps)
            VALUES ($steamId, $name, $rating, 0, 0, 0, 0, 0, 0, 0, 0)";
        insertCmd.Parameters.AddWithValue("$steamId", steamId);
        insertCmd.Parameters.AddWithValue("$name", name);
        insertCmd.Parameters.AddWithValue("$rating", initialRating);
        await insertCmd.ExecuteNonQueryAsync();

        return new PlayerData
        {
            SteamId = steamId,
            Name = name,
            Rating = initialRating
        };
    }

    /// <summary>Atualiza estatísticas e rating de um jogador após uma partida.</summary>
    public async Task UpdatePlayerAfterMatchAsync(string steamId, int newRating, bool won,
        int kills, int deaths, int assists, int damage, int mvps)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE players SET
                rating = $rating,
                matches = matches + 1,
                wins = wins + $winIncrement,
                losses = losses + $lossIncrement,
                kills = kills + $kills,
                deaths = deaths + $deaths,
                assists = assists + $assists,
                damage = damage + $damage,
                mvps = mvps + $mvps,
                updated_at = datetime('now')
            WHERE steamid = $steamId";

        command.Parameters.AddWithValue("$rating", newRating);
        command.Parameters.AddWithValue("$winIncrement", won ? 1 : 0);
        command.Parameters.AddWithValue("$lossIncrement", won ? 0 : 1);
        command.Parameters.AddWithValue("$kills", kills);
        command.Parameters.AddWithValue("$deaths", deaths);
        command.Parameters.AddWithValue("$assists", assists);
        command.Parameters.AddWithValue("$damage", damage);
        command.Parameters.AddWithValue("$mvps", mvps);
        command.Parameters.AddWithValue("$steamId", steamId);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Retorna os top N jogadores por rating.</summary>
    public async Task<List<PlayerData>> GetTopPlayersAsync(int count = 10)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT steamid, name, rating, matches, wins, losses, kills, deaths, assists, damage, mvps, created_at, updated_at
            FROM players
            WHERE matches > 0
            ORDER BY rating DESC
            LIMIT $count";
        command.Parameters.AddWithValue("$count", count);

        var players = new List<PlayerData>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            players.Add(ReadPlayerFromReader(reader));
        }
        return players;
    }

    /// <summary>Retorna a posição no ranking de um jogador.</summary>
    public async Task<int> GetPlayerRankPositionAsync(string steamId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*) + 1 FROM players
            WHERE matches > 0 AND rating > (SELECT rating FROM players WHERE steamid = $steamId)";
        command.Parameters.AddWithValue("$steamId", steamId);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>Insere um registro de partida e retorna o ID.</summary>
    public async Task<long> InsertMatchAsync(MatchRecord match)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO matches (match_guid, map, winner_team, ct_score, t_score, finished_at)
            VALUES ($matchGuid, $map, $winnerTeam, $ctScore, $tScore, $finishedAt);
            SELECT last_insert_rowid();";

        command.Parameters.AddWithValue("$matchGuid", match.MatchGuid);
        command.Parameters.AddWithValue("$map", match.Map);
        command.Parameters.AddWithValue("$winnerTeam", match.WinnerTeam);
        command.Parameters.AddWithValue("$ctScore", match.CtScore);
        command.Parameters.AddWithValue("$tScore", match.TScore);
        command.Parameters.AddWithValue("$finishedAt", match.FinishedAt.ToString("o"));

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    /// <summary>Insere um registro de mudança de rating.</summary>
    public async Task InsertRatingChangeAsync(RatingChange change)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO rating_history (match_id, steamid, old_rating, base_change, performance_swing, total_change, new_rating)
            VALUES ($matchId, $steamId, $oldRating, $baseChange, $performanceSwing, $totalChange, $newRating)";

        command.Parameters.AddWithValue("$matchId", change.MatchId);
        command.Parameters.AddWithValue("$steamId", change.SteamId);
        command.Parameters.AddWithValue("$oldRating", change.OldRating);
        command.Parameters.AddWithValue("$baseChange", change.BaseChange);
        command.Parameters.AddWithValue("$performanceSwing", change.PerformanceSwing);
        command.Parameters.AddWithValue("$totalChange", change.TotalChange);
        command.Parameters.AddWithValue("$newRating", change.NewRating);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Retorna o último rating change de um jogador.</summary>
    public async Task<RatingChange?> GetLastRatingChangeAsync(string steamId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT rh.id, rh.match_id, rh.steamid, rh.old_rating, rh.base_change,
                   rh.performance_swing, rh.total_change, rh.new_rating,
                   m.map, m.winner_team, m.ct_score, m.t_score, m.finished_at
            FROM rating_history rh
            JOIN matches m ON m.id = rh.match_id
            WHERE rh.steamid = $steamId
            ORDER BY rh.id DESC
            LIMIT 1";
        command.Parameters.AddWithValue("$steamId", steamId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new RatingChange
        {
            Id = reader.GetInt64(0),
            MatchId = reader.GetInt64(1),
            SteamId = reader.GetString(2),
            OldRating = reader.GetInt32(3),
            BaseChange = reader.GetInt32(4),
            PerformanceSwing = reader.GetInt32(5),
            TotalChange = reader.GetInt32(6),
            NewRating = reader.GetInt32(7)
        };
    }

    /// <summary>Retorna os últimos N rating changes de um jogador.</summary>
    public async Task<List<RatingChange>> GetRatingHistoryAsync(string steamId, int count = 10)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, match_id, steamid, old_rating, base_change, performance_swing, total_change, new_rating
            FROM rating_history
            WHERE steamid = $steamId
            ORDER BY id DESC
            LIMIT $count";
        command.Parameters.AddWithValue("$steamId", steamId);
        command.Parameters.AddWithValue("$count", count);

        var changes = new List<RatingChange>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            changes.Add(new RatingChange
            {
                Id = reader.GetInt64(0),
                MatchId = reader.GetInt64(1),
                SteamId = reader.GetString(2),
                OldRating = reader.GetInt32(3),
                BaseChange = reader.GetInt32(4),
                PerformanceSwing = reader.GetInt32(5),
                TotalChange = reader.GetInt32(6),
                NewRating = reader.GetInt32(7)
            });
        }
        return changes;
    }

    /// <summary>Retorna informações de uma partida pelo ID.</summary>
    public async Task<MatchRecord?> GetMatchByIdAsync(long matchId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, match_guid, map, winner_team, ct_score, t_score, finished_at FROM matches WHERE id = $matchId";
        command.Parameters.AddWithValue("$matchId", matchId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new MatchRecord
        {
            Id = reader.GetInt64(0),
            MatchGuid = reader.GetString(1),
            Map = reader.GetString(2),
            WinnerTeam = reader.GetInt32(3),
            CtScore = reader.GetInt32(4),
            TScore = reader.GetInt32(5),
            FinishedAt = DateTime.Parse(reader.GetString(6))
        };
    }

    /// <summary>Obtém um jogador pelo SteamID.</summary>
    public async Task<PlayerData?> GetPlayerAsync(string steamId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = "SELECT steamid, name, rating, matches, wins, losses, kills, deaths, assists, damage, mvps, created_at, updated_at FROM players WHERE steamid = $steamId";
        selectCmd.Parameters.AddWithValue("$steamId", steamId);

        await using var reader = await selectCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return ReadPlayerFromReader(reader);
    }

    /// <summary>Seta o rating de um jogador (admin).</summary>
    public async Task SetPlayerRatingAsync(string steamId, int rating)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE players SET rating = $rating, updated_at = datetime('now') WHERE steamid = $steamId";
        command.Parameters.AddWithValue("$rating", rating);
        command.Parameters.AddWithValue("$steamId", steamId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reseta completamente um jogador (admin).</summary>
    public async Task ResetPlayerAsync(string steamId, int initialRating)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE players SET
                rating = $rating, matches = 0, wins = 0, losses = 0,
                kills = 0, deaths = 0, assists = 0, damage = 0, mvps = 0,
                updated_at = datetime('now')
            WHERE steamid = $steamId";
        command.Parameters.AddWithValue("$rating", initialRating);
        command.Parameters.AddWithValue("$steamId", steamId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Retorna o total de jogadores com pelo menos uma partida.</summary>
    public async Task<int> GetTotalRankedPlayersAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM players WHERE matches > 0";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>Deleta todos os dados de todas as tabelas (limpa o banco de dados).</summary>
    public async Task ResetAllDataAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM rating_history;
            DELETE FROM matches;
            DELETE FROM players;
            VACUUM;
        ";
        await command.ExecuteNonQueryAsync();
    }

    private static PlayerData ReadPlayerFromReader(SqliteDataReader reader)
    {
        return new PlayerData
        {
            SteamId = reader.GetString(0),
            Name = reader.GetString(1),
            Rating = reader.GetInt32(2),
            Matches = reader.GetInt32(3),
            Wins = reader.GetInt32(4),
            Losses = reader.GetInt32(5),
            Kills = reader.GetInt32(6),
            Deaths = reader.GetInt32(7),
            Assists = reader.GetInt32(8),
            Damage = reader.GetInt64(9),
            Mvps = reader.GetInt32(10),
            CreatedAt = DateTime.Parse(reader.GetString(11)),
            UpdatedAt = DateTime.Parse(reader.GetString(12))
        };
    }
}
