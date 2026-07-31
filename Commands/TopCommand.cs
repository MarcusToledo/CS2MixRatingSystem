using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using MixRanking.Config;
using MixRanking.Services;

namespace MixRanking.Commands;

/// <summary>Comando !top — mostra os top 10 jogadores.</summary>
public class TopCommand
{
    private readonly PlayerService _playerService;
    private readonly RankingConfig _config;

    public TopCommand(PlayerService playerService, RankingConfig config)
    {
        _playerService = playerService;
        _config = config;
    }

    /// <summary>Registra o comando no plugin.</summary>
    public void Register(BasePlugin plugin)
    {
        plugin.AddCommand("css_top", "Mostra os Top 10 jogadores.", OnTopCommand);
    }

    private void OnTopCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid) return;

        int slot = player.Slot;

        Task.Run(async () =>
        {
            try
            {
                var topPlayers = await _playerService.GetTopPlayersAsync(10);

                Server.NextFrame(() =>
                {
                    var targetPlayer = Utilities.GetPlayerFromSlot(slot);
                    if (targetPlayer == null || !targetPlayer.IsValid) return;

                    if (topPlayers.Count == 0)
                    {
                        targetPlayer.PrintToChat($" {ChatColors.Gold}[{_config.ChatPrefix}]{ChatColors.Default} Nenhum jogador rankeado ainda.");
                        return;
                    }

                    targetPlayer.PrintToChat($" {ChatColors.Gold}🏆 ══════════ Top 10 {_config.ChatPrefix} ══════════ 🏆");

                    for (int i = 0; i < topPlayers.Count; i++)
                    {
                        var p = topPlayers[i];
                        double kd = p.Deaths > 0 ? (double)p.Kills / p.Deaths : p.Kills;
                        string posColor = i switch
                        {
                            0 => $"{ChatColors.Gold}🥇 #1",
                            1 => $"{ChatColors.Silver}🥈 #2",
                            2 => $"{ChatColors.Orange}🥉 #3",
                            _ => $"{ChatColors.Grey}   #{i + 1}"
                        };

                        targetPlayer.PrintToChat($" {posColor} {ChatColors.Default}{p.Name} {ChatColors.Grey}│ {ChatColors.Yellow}{p.Rating} pts {ChatColors.Grey}│ {ChatColors.Green}{p.Wins}V-{p.Losses}D {ChatColors.Grey}│ {ChatColors.Default}K/D: {ChatColors.Olive}{kd:F2}");
                    }

                    targetPlayer.PrintToChat($" {ChatColors.Gold}🏆 ═════════════════════════════════════ 🏆");
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_config.ChatPrefix}] Erro ao buscar top: {ex.Message}");
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
