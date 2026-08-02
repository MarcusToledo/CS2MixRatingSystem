using Microsoft.Data.Sqlite;
using MixRanking.Models;
using MixRanking.Rating;

namespace MixRanking.Database;

/// <summary>Serviço de persistência SQLite. Nenhuma regra de negócio aqui.</summary>
public class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
    }

    private const int CurrentSchemaVersion = 5;

    private const string Migration1_Baseline = @"
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

    private const string Migration2_MatchPlayerStats = @"
        CREATE TABLE IF NOT EXISTS match_player_stats (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            match_id INTEGER NOT NULL,
            steamid TEXT NOT NULL,
            team INTEGER NOT NULL,
            kills INTEGER NOT NULL DEFAULT 0,
            deaths INTEGER NOT NULL DEFAULT 0,
            assists INTEGER NOT NULL DEFAULT 0,
            damage INTEGER NOT NULL DEFAULT 0,
            rounds_played INTEGER NOT NULL DEFAULT 0,
            rounds_survived INTEGER NOT NULL DEFAULT 0,
            rounds_with_kill INTEGER NOT NULL DEFAULT 0,
            mvps INTEGER NOT NULL DEFAULT 0,
            opening_kills INTEGER NOT NULL DEFAULT 0,
            opening_deaths INTEGER NOT NULL DEFAULT 0,
            trade_kills INTEGER NOT NULL DEFAULT 0,
            flash_assists INTEGER NOT NULL DEFAULT 0,
            clutches_won INTEGER NOT NULL DEFAULT 0,
            headshots INTEGER NOT NULL DEFAULT 0,
            hs_percent REAL NOT NULL DEFAULT 0,
            adr REAL NOT NULL DEFAULT 0,
            kd_ratio REAL NOT NULL DEFAULT 0,
            kast_percent REAL NOT NULL DEFAULT 0,
            abandoned INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (match_id) REFERENCES matches(id),
            FOREIGN KEY (steamid) REFERENCES players(steamid)
        );
        CREATE INDEX IF NOT EXISTS idx_match_player_stats_steamid ON match_player_stats(steamid);
        CREATE INDEX IF NOT EXISTS idx_match_player_stats_match ON match_player_stats(match_id);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_match_player_stats_match_steamid ON match_player_stats(match_id, steamid);
    ";

    private const string Migration3_Seasons = @"
        CREATE TABLE IF NOT EXISTS seasons (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            started_at TEXT NOT NULL DEFAULT (datetime('now')),
            ended_at TEXT,
            is_active INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_seasons_single_active ON seasons(is_active) WHERE is_active = 1;

        CREATE TABLE IF NOT EXISTS season_ratings (
            season_id INTEGER NOT NULL,
            steamid TEXT NOT NULL,
            rating INTEGER NOT NULL DEFAULT 1000,
            matches INTEGER NOT NULL DEFAULT 0,
            wins INTEGER NOT NULL DEFAULT 0,
            losses INTEGER NOT NULL DEFAULT 0,
            updated_at TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (season_id, steamid),
            FOREIGN KEY (season_id) REFERENCES seasons(id),
            FOREIGN KEY (steamid) REFERENCES players(steamid)
        );

        INSERT INTO seasons (name, is_active) VALUES ('Season 1', 1);

        ALTER TABLE matches ADD COLUMN season_id INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE rating_history ADD COLUMN season_id INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE rating_history ADD COLUMN k_factor_used INTEGER;
        ALTER TABLE match_player_stats ADD COLUMN season_id INTEGER NOT NULL DEFAULT 1;
    ";

    private const string Migration4_AdminAuditLog = @"
        CREATE TABLE IF NOT EXISTS admin_audit_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            action TEXT NOT NULL,
            admin_steamid TEXT,
            admin_name TEXT NOT NULL DEFAULT 'CONSOLE',
            target_steamid TEXT,
            old_value INTEGER,
            new_value INTEGER,
            reason TEXT,
            created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE INDEX IF NOT EXISTS idx_admin_audit_log_target ON admin_audit_log(target_steamid);
        CREATE INDEX IF NOT EXISTS idx_admin_audit_log_created ON admin_audit_log(created_at);
    ";

    private const string Migration5_WebSyncQueue = @"
        CREATE TABLE IF NOT EXISTS web_sync_queue (
            steamid TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            rating INTEGER NOT NULL,
            matches INTEGER NOT NULL,
            wins INTEGER NOT NULL,
            losses INTEGER NOT NULL,
            kills INTEGER NOT NULL,
            deaths INTEGER NOT NULL,
            assists INTEGER NOT NULL,
            damage INTEGER NOT NULL,
            mvps INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS web_sync_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            wipe_pending INTEGER NOT NULL DEFAULT 0
        );
        INSERT INTO web_sync_state (id, wipe_pending) VALUES (1, 0);
    ";

    /// <summary>Cria as tabelas se não existirem rodando as migrações necessárias.</summary>
    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var walCommand = connection.CreateCommand();
        walCommand.CommandText = "PRAGMA journal_mode=WAL;";
        await walCommand.ExecuteNonQueryAsync();

        int version = await GetUserVersionAsync(connection);
        if (version < 1) { await RunMigrationAsync(connection, Migration1_Baseline); version = 1; await SetUserVersionAsync(connection, version); }
        if (version < 2) { await RunMigrationAsync(connection, Migration2_MatchPlayerStats); version = 2; await SetUserVersionAsync(connection, version); }
        if (version < 3) { await RunMigrationAsync(connection, Migration3_Seasons); version = 3; await SetUserVersionAsync(connection, version); }
        if (version < 4) { await RunMigrationAsync(connection, Migration4_AdminAuditLog); version = 4; await SetUserVersionAsync(connection, version); }
        if (version < 5) { await RunMigrationAsync(connection, Migration5_WebSyncQueue); version = 5; await SetUserVersionAsync(connection, version); }
    }

    private async Task<int> GetUserVersionAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private async Task SetUserVersionAsync(SqliteConnection connection, int version)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version};";
        await command.ExecuteNonQueryAsync();
    }

    private async Task RunMigrationAsync(SqliteConnection connection, string migrationSql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = migrationSql;
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
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

    /// <summary>Retorna o último rating change de um jogador.</summary>
    public async Task<RatingChange?> GetLastRatingChangeAsync(string steamId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT rh.id, rh.match_id, rh.steamid, rh.old_rating, rh.base_change,
                   rh.performance_swing, rh.total_change, rh.new_rating, rh.k_factor_used,
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
            NewRating = reader.GetInt32(7),
            KFactorUsed = reader.IsDBNull(8) ? null : reader.GetInt32(8)
        };
    }

    /// <summary>Retorna os últimos N rating changes de um jogador.</summary>
    public async Task<List<RatingChange>> GetRatingHistoryAsync(string steamId, int count = 10)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, match_id, steamid, old_rating, base_change, performance_swing, total_change, new_rating, k_factor_used
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
                NewRating = reader.GetInt32(7),
                KFactorUsed = reader.IsDBNull(8) ? null : reader.GetInt32(8)
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

    /// <summary>Retorna o total de rounds jogados acumulados de um jogador, somando todas as partidas registradas.</summary>
    public async Task<int> GetPlayerTotalRoundsPlayedAsync(string steamId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(rounds_played), 0) FROM match_player_stats WHERE steamid = $steamId";
        command.Parameters.AddWithValue("$steamId", steamId);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<Dictionary<string, PlayerData>> GetPlayersBySteamIdsAsync(List<string> steamIds)
    {
        var players = new Dictionary<string, PlayerData>();
        if (steamIds.Count == 0) return players;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        var parameterNames = new List<string>();
        for (int i = 0; i < steamIds.Count; i++)
        {
            var paramName = $"$steamId{i}";
            parameterNames.Add(paramName);
            command.Parameters.AddWithValue(paramName, steamIds[i]);
        }

        command.CommandText = $"SELECT steamid, name, rating, matches, wins, losses, kills, deaths, assists, damage, mvps, created_at, updated_at FROM players WHERE steamid IN ({string.Join(",", parameterNames)})";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var player = ReadPlayerFromReader(reader);
            players[player.SteamId] = player;
        }

        return players;
    }

    public async Task<int> GetActiveSeasonIdAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM seasons WHERE is_active = 1 LIMIT 1";
        var result = await command.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result) : 1;
    }

    /// <summary>Marca (upsert) o estado atual de um jogador como pendente de sync com a plataforma web.</summary>
    public async Task UpsertWebSyncQueueAsync(PlayerData player)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO web_sync_queue (steamid, name, rating, matches, wins, losses, kills, deaths, assists, damage, mvps, created_at)
            VALUES ($steamId, $name, $rating, $matches, $wins, $losses, $kills, $deaths, $assists, $damage, $mvps, $createdAt)
            ON CONFLICT(steamid) DO UPDATE SET
                name = $name, rating = $rating, matches = $matches, wins = $wins, losses = $losses,
                kills = $kills, deaths = $deaths, assists = $assists, damage = $damage, mvps = $mvps,
                created_at = $createdAt";
        command.Parameters.AddWithValue("$steamId", player.SteamId);
        command.Parameters.AddWithValue("$name", player.Name);
        command.Parameters.AddWithValue("$rating", player.Rating);
        command.Parameters.AddWithValue("$matches", player.Matches);
        command.Parameters.AddWithValue("$wins", player.Wins);
        command.Parameters.AddWithValue("$losses", player.Losses);
        command.Parameters.AddWithValue("$kills", player.Kills);
        command.Parameters.AddWithValue("$deaths", player.Deaths);
        command.Parameters.AddWithValue("$assists", player.Assists);
        command.Parameters.AddWithValue("$damage", player.Damage);
        command.Parameters.AddWithValue("$mvps", player.Mvps);
        command.Parameters.AddWithValue("$createdAt", player.CreatedAt.ToString("o"));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Retorna até <paramref name="limit"/> jogadores pendentes de sync com a plataforma web.</summary>
    public async Task<List<PlayerData>> GetPendingWebSyncEntriesAsync(int limit)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT steamid, name, rating, matches, wins, losses, kills, deaths, assists, damage, mvps, created_at
            FROM web_sync_queue
            LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<PlayerData>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new PlayerData
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
                CreatedAt = DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)
            });
        }
        return entries;
    }

    /// <summary>Remove da fila os jogadores já sincronizados com sucesso.</summary>
    public async Task ClearWebSyncQueueEntriesAsync(List<string> steamIds)
    {
        if (steamIds.Count == 0) return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        var parameterNames = new List<string>();
        for (int i = 0; i < steamIds.Count; i++)
        {
            var paramName = $"$steamId{i}";
            parameterNames.Add(paramName);
            command.Parameters.AddWithValue(paramName, steamIds[i]);
        }
        command.CommandText = $"DELETE FROM web_sync_queue WHERE steamid IN ({string.Join(",", parameterNames)})";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Indica se um !rating_wipe está pendente de sinalização à plataforma web.</summary>
    public async Task<bool> IsWipePendingAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT wipe_pending FROM web_sync_state WHERE id = 1";
        var result = await command.ExecuteScalarAsync();
        return result != null && Convert.ToInt32(result) == 1;
    }

    /// <summary>Marca que um wipe precisa ser sinalizado à plataforma web no próximo ciclo de sync.</summary>
    public async Task SetWipePendingAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE web_sync_state SET wipe_pending = 1 WHERE id = 1";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Limpa a sinalização de wipe pendente após envio bem-sucedido.</summary>
    public async Task ClearWipePendingAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE web_sync_state SET wipe_pending = 0 WHERE id = 1";
        await command.ExecuteNonQueryAsync();
    }

    public async Task WriteMatchEndResultAsync(MatchRecord match, List<MatchPlayerUpdate> updates, int initialRating, int activeSeasonId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // 1. Insert match
            var matchCmd = connection.CreateCommand();
            matchCmd.Transaction = (SqliteTransaction)transaction;
            matchCmd.CommandText = @"
                INSERT INTO matches (match_guid, map, winner_team, ct_score, t_score, finished_at, season_id)
                VALUES ($matchGuid, $map, $winnerTeam, $ctScore, $tScore, $finishedAt, $seasonId);
                SELECT last_insert_rowid();";

            matchCmd.Parameters.AddWithValue("$matchGuid", match.MatchGuid);
            matchCmd.Parameters.AddWithValue("$map", match.Map);
            matchCmd.Parameters.AddWithValue("$winnerTeam", match.WinnerTeam);
            matchCmd.Parameters.AddWithValue("$ctScore", match.CtScore);
            matchCmd.Parameters.AddWithValue("$tScore", match.TScore);
            matchCmd.Parameters.AddWithValue("$finishedAt", match.FinishedAt.ToString("o"));
            matchCmd.Parameters.AddWithValue("$seasonId", activeSeasonId);

            var matchIdResult = await matchCmd.ExecuteScalarAsync();
            long matchId = Convert.ToInt64(matchIdResult);

            foreach (var update in updates)
            {
                var stats = update.Stats;
                var playerData = update.PlayerData;
                var ratingChange = update.RatingChange;

                ratingChange.MatchId = matchId;

                // Check if player exists
                var checkCmd = connection.CreateCommand();
                checkCmd.Transaction = (SqliteTransaction)transaction;
                checkCmd.CommandText = "SELECT name FROM players WHERE steamid = $steamId";
                checkCmd.Parameters.AddWithValue("$steamId", playerData.SteamId);
                var existingNameObj = await checkCmd.ExecuteScalarAsync();

                if (existingNameObj == null)
                {
                    // Create new player in players table
                    var insertPlayerCmd = connection.CreateCommand();
                    insertPlayerCmd.Transaction = (SqliteTransaction)transaction;
                    insertPlayerCmd.CommandText = @"
                        INSERT INTO players (steamid, name, rating, matches, wins, losses, kills, deaths, assists, damage, mvps)
                        VALUES ($steamId, $name, $rating, 0, 0, 0, 0, 0, 0, 0, 0)";
                    insertPlayerCmd.Parameters.AddWithValue("$steamId", playerData.SteamId);
                    insertPlayerCmd.Parameters.AddWithValue("$name", stats.PlayerName);
                    insertPlayerCmd.Parameters.AddWithValue("$rating", playerData.Rating); // initial rating
                    await insertPlayerCmd.ExecuteNonQueryAsync();
                }
                else
                {
                    // Update name if changed
                    string existingName = (string)existingNameObj;
                    if (existingName != stats.PlayerName)
                    {
                        var updateNameCmd = connection.CreateCommand();
                        updateNameCmd.Transaction = (SqliteTransaction)transaction;
                        updateNameCmd.CommandText = "UPDATE players SET name = $name, updated_at = datetime('now') WHERE steamid = $steamId";
                        updateNameCmd.Parameters.AddWithValue("$name", stats.PlayerName);
                        updateNameCmd.Parameters.AddWithValue("$steamId", playerData.SteamId);
                        await updateNameCmd.ExecuteNonQueryAsync();
                    }
                }

                // 2. Update players
                var updatePlayerCmd = connection.CreateCommand();
                updatePlayerCmd.Transaction = (SqliteTransaction)transaction;
                updatePlayerCmd.CommandText = @"
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

                updatePlayerCmd.Parameters.AddWithValue("$rating", update.NewRating);
                updatePlayerCmd.Parameters.AddWithValue("$winIncrement", update.Won ? 1 : 0);
                updatePlayerCmd.Parameters.AddWithValue("$lossIncrement", update.Won ? 0 : 1);
                updatePlayerCmd.Parameters.AddWithValue("$kills", stats.Kills);
                updatePlayerCmd.Parameters.AddWithValue("$deaths", stats.Deaths);
                updatePlayerCmd.Parameters.AddWithValue("$assists", stats.Assists);
                updatePlayerCmd.Parameters.AddWithValue("$damage", stats.Damage);
                updatePlayerCmd.Parameters.AddWithValue("$mvps", stats.Mvps);
                updatePlayerCmd.Parameters.AddWithValue("$steamId", playerData.SteamId);

                await updatePlayerCmd.ExecuteNonQueryAsync();

                // 3. Insert rating_history
                var insertRatingCmd = connection.CreateCommand();
                insertRatingCmd.Transaction = (SqliteTransaction)transaction;
                insertRatingCmd.CommandText = @"
                    INSERT INTO rating_history (match_id, steamid, old_rating, base_change, performance_swing, total_change, new_rating, season_id, k_factor_used)
                    VALUES ($matchId, $steamId, $oldRating, $baseChange, $performanceSwing, $totalChange, $newRating, $seasonId, $kFactorUsed)";

                insertRatingCmd.Parameters.AddWithValue("$matchId", matchId);
                insertRatingCmd.Parameters.AddWithValue("$steamId", playerData.SteamId);
                insertRatingCmd.Parameters.AddWithValue("$oldRating", ratingChange.OldRating);
                insertRatingCmd.Parameters.AddWithValue("$baseChange", ratingChange.BaseChange);
                insertRatingCmd.Parameters.AddWithValue("$performanceSwing", ratingChange.PerformanceSwing);
                insertRatingCmd.Parameters.AddWithValue("$totalChange", ratingChange.TotalChange);
                insertRatingCmd.Parameters.AddWithValue("$newRating", ratingChange.NewRating);
                insertRatingCmd.Parameters.AddWithValue("$seasonId", activeSeasonId);
                insertRatingCmd.Parameters.AddWithValue("$kFactorUsed", (object?)ratingChange.KFactorUsed ?? DBNull.Value);

                await insertRatingCmd.ExecuteNonQueryAsync();

                // 4. Insert match_player_stats
                var insertStatsCmd = connection.CreateCommand();
                insertStatsCmd.Transaction = (SqliteTransaction)transaction;
                insertStatsCmd.CommandText = @"
                    INSERT INTO match_player_stats (
                        match_id, steamid, team, kills, deaths, assists, damage,
                        rounds_played, rounds_survived, rounds_with_kill, mvps,
                        opening_kills, opening_deaths, trade_kills, flash_assists,
                        clutches_won, headshots, hs_percent, adr, kd_ratio,
                        kast_percent, abandoned, season_id
                    ) VALUES (
                        $matchId, $steamId, $team, $kills, $deaths, $assists, $damage,
                        $roundsPlayed, $roundsSurvived, $roundsWithKill, $mvps,
                        $openingKills, $openingDeaths, $tradeKills, $flashAssists,
                        $clutchesWon, $headshots, $hsPercent, $adr, $kdRatio,
                        $kastPercent, $abandoned, $seasonId
                    )";

                double kastPercent = SwingCalculator.CalculateKastPercent(stats);

                insertStatsCmd.Parameters.AddWithValue("$matchId", matchId);
                insertStatsCmd.Parameters.AddWithValue("$steamId", playerData.SteamId);
                insertStatsCmd.Parameters.AddWithValue("$team", (int)stats.Team);
                insertStatsCmd.Parameters.AddWithValue("$kills", stats.Kills);
                insertStatsCmd.Parameters.AddWithValue("$deaths", stats.Deaths);
                insertStatsCmd.Parameters.AddWithValue("$assists", stats.Assists);
                insertStatsCmd.Parameters.AddWithValue("$damage", stats.Damage);
                insertStatsCmd.Parameters.AddWithValue("$roundsPlayed", stats.RoundsPlayed);
                insertStatsCmd.Parameters.AddWithValue("$roundsSurvived", stats.RoundsSurvived);
                insertStatsCmd.Parameters.AddWithValue("$roundsWithKill", stats.RoundsWithKill);
                insertStatsCmd.Parameters.AddWithValue("$mvps", stats.Mvps);
                insertStatsCmd.Parameters.AddWithValue("$openingKills", stats.OpeningKills);
                insertStatsCmd.Parameters.AddWithValue("$openingDeaths", stats.OpeningDeaths);
                insertStatsCmd.Parameters.AddWithValue("$tradeKills", stats.TradeKills);
                insertStatsCmd.Parameters.AddWithValue("$flashAssists", stats.FlashAssists);
                insertStatsCmd.Parameters.AddWithValue("$clutchesWon", 0);
                insertStatsCmd.Parameters.AddWithValue("$headshots", 0);
                insertStatsCmd.Parameters.AddWithValue("$hsPercent", 0.0);
                insertStatsCmd.Parameters.AddWithValue("$adr", stats.Adr);
                insertStatsCmd.Parameters.AddWithValue("$kdRatio", stats.KdRatio);
                insertStatsCmd.Parameters.AddWithValue("$kastPercent", kastPercent);
                insertStatsCmd.Parameters.AddWithValue("$abandoned", stats.Abandoned ? 1 : 0);
                insertStatsCmd.Parameters.AddWithValue("$seasonId", activeSeasonId);

                await insertStatsCmd.ExecuteNonQueryAsync();

                // 5. Upsert season_ratings
                var upsertSeasonCmd = connection.CreateCommand();
                upsertSeasonCmd.Transaction = (SqliteTransaction)transaction;
                upsertSeasonCmd.CommandText = @"
                    INSERT INTO season_ratings (season_id, steamid, rating, matches, wins, losses, updated_at)
                    VALUES ($seasonId, $steamId, $initialRating + $totalChange, 1, $winIncrement, $lossIncrement, datetime('now'))
                    ON CONFLICT(season_id, steamid) DO UPDATE SET
                        rating = rating + $totalChange,
                        matches = matches + 1,
                        wins = wins + $winIncrement,
                        losses = losses + $lossIncrement,
                        updated_at = datetime('now')";

                upsertSeasonCmd.Parameters.AddWithValue("$seasonId", activeSeasonId);
                upsertSeasonCmd.Parameters.AddWithValue("$steamId", playerData.SteamId);
                upsertSeasonCmd.Parameters.AddWithValue("$initialRating", initialRating);
                upsertSeasonCmd.Parameters.AddWithValue("$totalChange", ratingChange.TotalChange);
                upsertSeasonCmd.Parameters.AddWithValue("$winIncrement", update.Won ? 1 : 0);
                upsertSeasonCmd.Parameters.AddWithValue("$lossIncrement", update.Won ? 0 : 1);

                await upsertSeasonCmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task SetPlayerRatingWithAuditAsync(string targetSteamId, int newRating, string? adminSteamId, string adminName, string? reason)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var getCmd = connection.CreateCommand();
            getCmd.Transaction = (SqliteTransaction)transaction;
            getCmd.CommandText = "SELECT rating FROM players WHERE steamid = $steamId";
            getCmd.Parameters.AddWithValue("$steamId", targetSteamId);
            var result = await getCmd.ExecuteScalarAsync();
            if (result == null)
            {
                throw new Exception("Jogador não encontrado.");
            }
            int oldRating = Convert.ToInt32(result);

            var updateCmd = connection.CreateCommand();
            updateCmd.Transaction = (SqliteTransaction)transaction;
            updateCmd.CommandText = "UPDATE players SET rating = $rating, updated_at = datetime('now') WHERE steamid = $steamId";
            updateCmd.Parameters.AddWithValue("$rating", newRating);
            updateCmd.Parameters.AddWithValue("$steamId", targetSteamId);
            await updateCmd.ExecuteNonQueryAsync();

            var auditCmd = connection.CreateCommand();
            auditCmd.Transaction = (SqliteTransaction)transaction;
            auditCmd.CommandText = @"
                INSERT INTO admin_audit_log (action, admin_steamid, admin_name, target_steamid, old_value, new_value, reason)
                VALUES ('SET', $adminSteamId, $adminName, $targetSteamId, $oldValue, $newValue, $reason)";
            auditCmd.Parameters.AddWithValue("$adminSteamId", (object?)adminSteamId ?? DBNull.Value);
            auditCmd.Parameters.AddWithValue("$adminName", adminName);
            auditCmd.Parameters.AddWithValue("$targetSteamId", targetSteamId);
            auditCmd.Parameters.AddWithValue("$oldValue", oldRating);
            auditCmd.Parameters.AddWithValue("$newValue", newRating);
            auditCmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            await auditCmd.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task ResetPlayerWithAuditAsync(string targetSteamId, int initialRating, string? adminSteamId, string adminName, string? reason)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var getCmd = connection.CreateCommand();
            getCmd.Transaction = (SqliteTransaction)transaction;
            getCmd.CommandText = "SELECT rating FROM players WHERE steamid = $steamId";
            getCmd.Parameters.AddWithValue("$steamId", targetSteamId);
            var result = await getCmd.ExecuteScalarAsync();
            if (result == null)
            {
                throw new Exception("Jogador não encontrado.");
            }
            int oldRating = Convert.ToInt32(result);

            var resetCmd = connection.CreateCommand();
            resetCmd.Transaction = (SqliteTransaction)transaction;
            resetCmd.CommandText = @"
                UPDATE players SET
                    rating = $rating, matches = 0, wins = 0, losses = 0,
                    kills = 0, deaths = 0, assists = 0, damage = 0, mvps = 0,
                    updated_at = datetime('now')
                WHERE steamid = $steamId";
            resetCmd.Parameters.AddWithValue("$rating", initialRating);
            resetCmd.Parameters.AddWithValue("$steamId", targetSteamId);
            await resetCmd.ExecuteNonQueryAsync();

            var auditCmd = connection.CreateCommand();
            auditCmd.Transaction = (SqliteTransaction)transaction;
            auditCmd.CommandText = @"
                INSERT INTO admin_audit_log (action, admin_steamid, admin_name, target_steamid, old_value, new_value, reason)
                VALUES ('RESET', $adminSteamId, $adminName, $targetSteamId, $oldValue, $newValue, $reason)";
            auditCmd.Parameters.AddWithValue("$adminSteamId", (object?)adminSteamId ?? DBNull.Value);
            auditCmd.Parameters.AddWithValue("$adminName", adminName);
            auditCmd.Parameters.AddWithValue("$targetSteamId", targetSteamId);
            auditCmd.Parameters.AddWithValue("$oldValue", oldRating);
            auditCmd.Parameters.AddWithValue("$newValue", initialRating);
            auditCmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            await auditCmd.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task AdjustPlayerRatingWithAuditAsync(string targetSteamId, int amount, bool isAdd, int minRating, string? adminSteamId, string adminName, string? reason)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var getCmd = connection.CreateCommand();
            getCmd.Transaction = (SqliteTransaction)transaction;
            getCmd.CommandText = "SELECT rating FROM players WHERE steamid = $steamId";
            getCmd.Parameters.AddWithValue("$steamId", targetSteamId);
            var result = await getCmd.ExecuteScalarAsync();
            if (result == null)
            {
                throw new Exception("Jogador não encontrado.");
            }
            int oldRating = Convert.ToInt32(result);

            int newRating = isAdd ? (oldRating + amount) : Math.Max(oldRating - amount, minRating);

            var updateCmd = connection.CreateCommand();
            updateCmd.Transaction = (SqliteTransaction)transaction;
            updateCmd.CommandText = "UPDATE players SET rating = $rating, updated_at = datetime('now') WHERE steamid = $steamId";
            updateCmd.Parameters.AddWithValue("$rating", newRating);
            updateCmd.Parameters.AddWithValue("$steamId", targetSteamId);
            await updateCmd.ExecuteNonQueryAsync();

            var auditCmd = connection.CreateCommand();
            auditCmd.Transaction = (SqliteTransaction)transaction;
            auditCmd.CommandText = @"
                INSERT INTO admin_audit_log (action, admin_steamid, admin_name, target_steamid, old_value, new_value, reason)
                VALUES ($action, $adminSteamId, $adminName, $targetSteamId, $oldValue, $newValue, $reason)";
            auditCmd.Parameters.AddWithValue("$action", isAdd ? "ADD" : "REMOVE");
            auditCmd.Parameters.AddWithValue("$adminSteamId", (object?)adminSteamId ?? DBNull.Value);
            auditCmd.Parameters.AddWithValue("$adminName", adminName);
            auditCmd.Parameters.AddWithValue("$targetSteamId", targetSteamId);
            auditCmd.Parameters.AddWithValue("$oldValue", oldRating);
            auditCmd.Parameters.AddWithValue("$newValue", newRating);
            auditCmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            await auditCmd.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task ResetAllDataWithAuditAsync(string? adminSteamId, string adminName, string? reason)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            try
            {
                var auditCmd = connection.CreateCommand();
                auditCmd.Transaction = (SqliteTransaction)transaction;
                auditCmd.CommandText = @"
                    INSERT INTO admin_audit_log (action, admin_steamid, admin_name, target_steamid, old_value, new_value, reason)
                    VALUES ('WIPE', $adminSteamId, $adminName, NULL, NULL, NULL, $reason)";
                auditCmd.Parameters.AddWithValue("$adminSteamId", (object?)adminSteamId ?? DBNull.Value);
                auditCmd.Parameters.AddWithValue("$adminName", adminName);
                auditCmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                await auditCmd.ExecuteNonQueryAsync();

                var deleteCmd = connection.CreateCommand();
                deleteCmd.Transaction = (SqliteTransaction)transaction;
                deleteCmd.CommandText = @"
                    DELETE FROM rating_history;
                    DELETE FROM match_player_stats;
                    DELETE FROM season_ratings;
                    DELETE FROM matches;
                    DELETE FROM players;
                    DELETE FROM web_sync_queue;
                    UPDATE web_sync_state SET wipe_pending = 1 WHERE id = 1;
                ";
                await deleteCmd.ExecuteNonQueryAsync();

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        var vacuumCmd = connection.CreateCommand();
        vacuumCmd.CommandText = "VACUUM;";
        await vacuumCmd.ExecuteNonQueryAsync();
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
            CreatedAt = DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal),
            UpdatedAt = DateTime.Parse(reader.GetString(12), null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)
        };
    }
}
