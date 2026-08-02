using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using MixRanking.Config;
using MixRanking.Database;
using MixRanking.Services;

namespace MixRanking.Commands;

/// <summary>Comandos administrativos para gerenciar ratings.</summary>
public class AdminCommands
{
    private readonly DatabaseService _db;
    private readonly RankingConfig _config;
    private readonly WebSyncService _webSyncService;

    public AdminCommands(DatabaseService db, RankingConfig config, WebSyncService webSyncService)
    {
        _db = db;
        _config = config;
        _webSyncService = webSyncService;
    }

    /// <summary>Busca o estado atual do jogador após uma alteração de rating e marca para sync com a plataforma web.</summary>
    private async Task MarkPlayerDirtyForWebSyncAsync(string steamId)
    {
        var updatedPlayer = await _db.GetPlayerAsync(steamId);
        if (updatedPlayer != null)
        {
            await _webSyncService.MarkDirtyAsync(updatedPlayer);
        }
    }

    /// <summary>Registra os comandos admin no plugin.</summary>
    public void Register(BasePlugin plugin)
    {
        plugin.AddCommand("css_rating_set", "[ADMIN] Seta o rating de um jogador.", OnRatingSet);
        plugin.AddCommand("css_rating_reset", "[ADMIN] Reseta um jogador.", OnRatingReset);
        plugin.AddCommand("css_rating_add", "[ADMIN] Adiciona pontos de rating.", OnRatingAdd);
        plugin.AddCommand("css_rating_remove", "[ADMIN] Remove pontos de rating.", OnRatingRemove);
        plugin.AddCommand("css_rating_wipe", "[ADMIN] Reseta completamente o ranking (deleta todos os dados).", OnRatingWipe);
    }

    [RequiresPermissions("@css/root")]
    private void OnRatingSet(CCSPlayerController? player, CommandInfo command)
    {
        if (!ValidateAdmin(player)) return;

        if (command.ArgCount < 3)
        {
            PrintToPlayerDirect(player, $"{ChatColors.Red}Uso: !rating_set <steamid64> <valor> [motivo...]");
            return;
        }

        string targetSteamId = command.ArgByIndex(1);
        if (!int.TryParse(command.ArgByIndex(2), out int newRating))
        {
            PrintToPlayerDirect(player, $"{ChatColors.Red}Valor inválido.");
            return;
        }

        newRating = Math.Max(newRating, _config.MinRating);
        int? slot = player?.Slot;
        string? adminSteamId = player?.SteamID.ToString();
        string adminName = player?.PlayerName ?? "CONSOLE";

        string? reason = null;
        if (command.ArgCount > 3)
        {
            var reasonArgs = new List<string>();
            for (int i = 3; i < command.ArgCount; i++)
            {
                reasonArgs.Add(command.ArgByIndex(i));
            }
            reason = string.Join(" ", reasonArgs);
        }

        Task.Run(async () =>
        {
            try
            {
                var targetPlayer = await _db.GetPlayerAsync(targetSteamId);
                if (targetPlayer == null)
                {
                    PrintToPlayer(slot, $"{ChatColors.Red}Jogador não encontrado no banco.");
                    return;
                }

                await _db.SetPlayerRatingWithAuditAsync(targetSteamId, newRating, adminSteamId, adminName, reason);
                await MarkPlayerDirtyForWebSyncAsync(targetSteamId);
                PrintToPlayer(slot, $"{ChatColors.Green}Rating de {targetPlayer.Name} setado para {newRating}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GurizadaMix] Erro ao setar rating: {ex.Message}");
                PrintToPlayer(slot, $"{ChatColors.Red}Erro ao setar rating.");
            }
        });
    }

    [RequiresPermissions("@css/root")]
    private void OnRatingReset(CCSPlayerController? player, CommandInfo command)
    {
        if (!ValidateAdmin(player)) return;

        if (command.ArgCount < 2)
        {
            PrintToPlayerDirect(player, $"{ChatColors.Red}Uso: !rating_reset <steamid64> [motivo...]");
            return;
        }

        string targetSteamId = command.ArgByIndex(1);
        int? slot = player?.Slot;
        string? adminSteamId = player?.SteamID.ToString();
        string adminName = player?.PlayerName ?? "CONSOLE";

        string? reason = null;
        if (command.ArgCount > 2)
        {
            var reasonArgs = new List<string>();
            for (int i = 2; i < command.ArgCount; i++)
            {
                reasonArgs.Add(command.ArgByIndex(i));
            }
            reason = string.Join(" ", reasonArgs);
        }

        Task.Run(async () =>
        {
            try
            {
                var targetPlayer = await _db.GetPlayerAsync(targetSteamId);
                if (targetPlayer == null)
                {
                    PrintToPlayer(slot, $"{ChatColors.Red}Jogador não encontrado no banco.");
                    return;
                }

                await _db.ResetPlayerWithAuditAsync(targetSteamId, _config.InitialRating, adminSteamId, adminName, reason);
                await MarkPlayerDirtyForWebSyncAsync(targetSteamId);
                PrintToPlayer(slot, $"{ChatColors.Green}Jogador {targetPlayer.Name} resetado para {_config.InitialRating}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GurizadaMix] Erro ao resetar jogador: {ex.Message}");
                PrintToPlayer(slot, $"{ChatColors.Red}Erro ao resetar jogador.");
            }
        });
    }

    [RequiresPermissions("@css/root")]
    private void OnRatingAdd(CCSPlayerController? player, CommandInfo command)
    {
        if (!ValidateAdmin(player)) return;

        if (command.ArgCount < 3)
        {
            PrintToPlayerDirect(player, $"{ChatColors.Red}Uso: !rating_add <steamid64> <valor> [motivo...]");
            return;
        }

        string targetSteamId = command.ArgByIndex(1);
        if (!int.TryParse(command.ArgByIndex(2), out int amount) || amount <= 0)
        {
            PrintToPlayerDirect(player, $"{ChatColors.Red}Valor inválido (deve ser positivo).");
            return;
        }

        int? slot = player?.Slot;
        string? adminSteamId = player?.SteamID.ToString();
        string adminName = player?.PlayerName ?? "CONSOLE";

        string? reason = null;
        if (command.ArgCount > 3)
        {
            var reasonArgs = new List<string>();
            for (int i = 3; i < command.ArgCount; i++)
            {
                reasonArgs.Add(command.ArgByIndex(i));
            }
            reason = string.Join(" ", reasonArgs);
        }

        Task.Run(async () =>
        {
            try
            {
                var targetPlayer = await _db.GetPlayerAsync(targetSteamId);
                if (targetPlayer == null)
                {
                    PrintToPlayer(slot, $"{ChatColors.Red}Jogador não encontrado no banco.");
                    return;
                }

                await _db.AdjustPlayerRatingWithAuditAsync(targetSteamId, amount, isAdd: true, minRating: _config.MinRating, adminSteamId: adminSteamId, adminName: adminName, reason: reason);
                await MarkPlayerDirtyForWebSyncAsync(targetSteamId);
                int newRating = targetPlayer.Rating + amount;
                PrintToPlayer(slot, $"{ChatColors.Green}+{amount} rating para {targetPlayer.Name}. Novo: {newRating}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GurizadaMix] Erro ao adicionar rating: {ex.Message}");
                PrintToPlayer(slot, $"{ChatColors.Red}Erro ao adicionar rating.");
            }
        });
    }

    [RequiresPermissions("@css/root")]
    private void OnRatingRemove(CCSPlayerController? player, CommandInfo command)
    {
        if (!ValidateAdmin(player)) return;

        if (command.ArgCount < 3)
        {
            PrintToPlayerDirect(player, $"{ChatColors.Red}Uso: !rating_remove <steamid64> <valor> [motivo...]");
            return;
        }

        string targetSteamId = command.ArgByIndex(1);
        if (!int.TryParse(command.ArgByIndex(2), out int amount) || amount <= 0)
        {
            PrintToPlayerDirect(player, $"{ChatColors.Red}Valor inválido (deve ser positivo).");
            return;
        }

        int? slot = player?.Slot;
        string? adminSteamId = player?.SteamID.ToString();
        string adminName = player?.PlayerName ?? "CONSOLE";

        string? reason = null;
        if (command.ArgCount > 3)
        {
            var reasonArgs = new List<string>();
            for (int i = 3; i < command.ArgCount; i++)
            {
                reasonArgs.Add(command.ArgByIndex(i));
            }
            reason = string.Join(" ", reasonArgs);
        }

        Task.Run(async () =>
        {
            try
            {
                var targetPlayer = await _db.GetPlayerAsync(targetSteamId);
                if (targetPlayer == null)
                {
                    PrintToPlayer(slot, $"{ChatColors.Red}Jogador não encontrado no banco.");
                    return;
                }

                await _db.AdjustPlayerRatingWithAuditAsync(targetSteamId, amount, isAdd: false, minRating: _config.MinRating, adminSteamId: adminSteamId, adminName: adminName, reason: reason);
                await MarkPlayerDirtyForWebSyncAsync(targetSteamId);
                int newRating = Math.Max(targetPlayer.Rating - amount, _config.MinRating);
                PrintToPlayer(slot, $"{ChatColors.Green}-{amount} rating de {targetPlayer.Name}. Novo: {newRating}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GurizadaMix] Erro ao remover rating: {ex.Message}");
                PrintToPlayer(slot, $"{ChatColors.Red}Erro ao remover rating.");
            }
        });
    }

    [RequiresPermissions("@css/root")]
    private void OnRatingWipe(CCSPlayerController? player, CommandInfo command)
    {
        if (!ValidateAdmin(player)) return;

        int? slot = player?.Slot;
        string? adminSteamId = player?.SteamID.ToString();
        string adminName = player?.PlayerName ?? "CONSOLE";

        string? reason = null;
        if (command.ArgCount > 1)
        {
            var reasonArgs = new List<string>();
            for (int i = 1; i < command.ArgCount; i++)
            {
                reasonArgs.Add(command.ArgByIndex(i));
            }
            reason = string.Join(" ", reasonArgs);
        }

        Task.Run(async () =>
        {
            try
            {
                await _db.ResetAllDataWithAuditAsync(adminSteamId, adminName, reason);
                PrintToPlayer(slot, $"{ChatColors.Green}Ranking completamente resetado! Todos os dados de jogadores e partidas foram eliminados.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_config.ChatPrefix}] Erro ao limpar banco de dados: {ex.Message}");
                PrintToPlayer(slot, $"{ChatColors.Red}Erro ao resetar o ranking.");
            }
        });
    }

    /// <summary>Valida se quem executou tem permissão de admin.</summary>
    private bool ValidateAdmin(CCSPlayerController? player)
    {
        if (player == null) return true; // Server console always allowed

        if (!AdminManager.PlayerHasPermissions(player, "@css/root"))
        {
            player.PrintToChat($" {ChatColors.Red}[GurizadaMix] Sem permissão.");
            return false;
        }
        return true;
    }

    /// <summary>Envia mensagem de forma segura no thread principal após tarefa background.</summary>
    private void PrintToPlayer(int? slot, string message)
    {
        Server.NextFrame(() =>
        {
            if (slot.HasValue)
            {
                var targetPlayer = Utilities.GetPlayerFromSlot(slot.Value);
                if (targetPlayer != null && targetPlayer.IsValid)
                {
                    targetPlayer.PrintToChat($" {ChatColors.Gold}[GurizadaMix]{ChatColors.Default} {message}");
                }
            }
            else
            {
                Console.WriteLine($"[GurizadaMix] {message}");
            }
        });
    }

    /// <summary>Envia mensagem diretamente (usada fora de Task.Run, ou seja, no thread principal).</summary>
    private void PrintToPlayerDirect(CCSPlayerController? player, string message)
    {
        if (player != null && player.IsValid)
            player.PrintToChat($" {ChatColors.Gold}[GurizadaMix]{ChatColors.Default} {message}");
        else
            Console.WriteLine($"[GurizadaMix] {message}");
    }
}
