using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using MixRanking.Config;
using MixRanking.Services;

namespace MixRanking.Commands;

/// <summary>Comando !rank — mostra o ranking do jogador.</summary>
public class RankCommand
{
    private readonly PlayerService _playerService;
    private readonly RankingConfig _config;

    public RankCommand(PlayerService playerService, RankingConfig config)
    {
        _playerService = playerService;
        _config = config;
    }

    /// <summary>Registra o comando no plugin.</summary>
    public void Register(BasePlugin plugin)
    {
        plugin.AddCommand("css_rank", "Mostra seu ranking atual.", OnRankCommand);
    }

    private void OnRankCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid) return;

        string steamId = player.SteamID.ToString();
        int slot = player.Slot;

        Task.Run(async () =>
        {
            try
            {
                var playerData = await _playerService.GetPlayerAsync(steamId);
                int rankPosition = 0;
                int totalPlayers = 0;
                int totalRounds = 0;

                if (playerData != null && playerData.Matches > 0)
                {
                    rankPosition = await _playerService.GetRankPositionAsync(steamId);
                    totalPlayers = await _playerService.GetTotalRankedPlayersAsync();
                    totalRounds = await _playerService.GetTotalRoundsPlayedAsync(steamId);
                }

                Server.NextFrame(() =>
                {
                    var targetPlayer = Utilities.GetPlayerFromSlot(slot);
                    if (targetPlayer == null || !targetPlayer.IsValid) return;

                    if (playerData == null || playerData.Matches == 0)
                    {
                        targetPlayer.PrintToChat($" {ChatColors.Gold}[{_config.ChatPrefix}]{ChatColors.Default} Você ainda não jogou nenhuma partida rankeada.");
                        return;
                    }

                    double kd = playerData.Deaths > 0 ? (double)playerData.Kills / playerData.Deaths : playerData.Kills;
                    double adr = totalRounds > 0 ? (double)playerData.Damage / totalRounds : 0;
                    double winrate = playerData.Matches > 0 ? (double)playerData.Wins / playerData.Matches * 100 : 0;

                    targetPlayer.PrintToChat($" {ChatColors.Gold}★ ════════════════════════════════════════ ★");
                    targetPlayer.PrintToChat($" {ChatColors.Gold}[{_config.ChatPrefix}] {ChatColors.Default}Jogador: {ChatColors.Green}{playerData.Name}");
                    targetPlayer.PrintToChat($" {ChatColors.Gold}» {ChatColors.Default}Rating: {ChatColors.Yellow}{playerData.Rating} {ChatColors.Grey}│ {ChatColors.Default}Rank: {ChatColors.Yellow}#{rankPosition} {ChatColors.Grey}(de {totalPlayers})");
                    targetPlayer.PrintToChat($" {ChatColors.Gold}» {ChatColors.Default}Partidas: {ChatColors.Default}{playerData.Matches} {ChatColors.Grey}│ {ChatColors.Default}Winrate: {ChatColors.Lime}{winrate:F1}% {ChatColors.Grey}({playerData.Wins}V - {playerData.Losses}D)");
                    targetPlayer.PrintToChat($" {ChatColors.Gold}» {ChatColors.Default}K/D: {ChatColors.Olive}{kd:F2} {ChatColors.Grey}│ {ChatColors.Default}ADR: {ChatColors.Olive}{adr:F1}");
                    targetPlayer.PrintToChat($" {ChatColors.Gold}★ ════════════════════════════════════════ ★");
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_config.ChatPrefix}] Erro ao buscar ranking: {ex.Message}");
                Server.NextFrame(() =>
                {
                    var targetPlayer = Utilities.GetPlayerFromSlot(slot);
                    if (targetPlayer != null && targetPlayer.IsValid)
                    {
                        targetPlayer.PrintToChat($" {ChatColors.Red}[{_config.ChatPrefix}] Erro ao buscar ranking.");
                    }
                });
            }
        });
    }
}
