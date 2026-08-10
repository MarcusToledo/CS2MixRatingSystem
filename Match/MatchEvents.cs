using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using GameModeManager.Shared;
using MixRanking.Models;
using MixRanking.Services;
using MixRanking.Config;
using Microsoft.Extensions.Logging;

namespace MixRanking.Match;

/// <summary>
/// Registra e processa todos os event handlers do CS2.
/// Delega estatísticas ao StatisticsService e estado ao MatchService.
/// </summary>
public class MatchEvents
{
    private readonly MatchService _matchService;
    private readonly StatisticsService _statistics;
    private readonly ILogger _logger;
    private readonly RankingConfig _config;
    private readonly PluginCapability<IGameModeApi?> _gameModeCapability = new("game_mode:api");

    public MatchEvents(MatchService matchService, StatisticsService statistics, RankingConfig config, ILogger logger)
    {
        _matchService = matchService;
        _statistics = statistics;
        _config = config;
        _logger = logger;
    }

    /// <summary>Registra todos os event handlers no plugin.</summary>
    public void RegisterEvents(BasePlugin plugin)
    {
        plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        plugin.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        plugin.RegisterEventHandler<EventRoundAnnounceMatchStart>(OnRoundAnnounceMatchStart);
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        plugin.RegisterEventHandler<EventRoundMvp>(OnRoundMvp);
        plugin.RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        plugin.RegisterEventHandler<EventBombDefused>(OnBombDefused);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        plugin.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);

        _logger.LogInformation("[MixRanking] Event handlers registered.");
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!_matchService.IsLive) return HookResult.Continue;

        var victim = @event.Userid;
        var attacker = @event.Attacker;
        var assister = @event.Assister;

        if (victim == null || !victim.IsValid || victim.IsBot) return HookResult.Continue;

        ulong victimSteamId = victim.SteamID;
        CsTeam victimTeam = (CsTeam)victim.TeamNum;

        // Ensure victim stats exist
        _statistics.GetOrCreateStats(victimSteamId, victim.PlayerName, victimTeam);

        if (attacker != null && attacker.IsValid && !attacker.IsBot && attacker != victim)
        {
            ulong attackerSteamId = attacker.SteamID;
            CsTeam attackerTeam = (CsTeam)attacker.TeamNum;

            // Only count if different teams (no team kills)
            if (attackerTeam != victimTeam)
            {
                _statistics.GetOrCreateStats(attackerSteamId, attacker.PlayerName, attackerTeam);
                bool flashAssist = @event.Assistedflash;
                _statistics.RecordKill(attackerSteamId, victimSteamId, victimTeam, flashAssist);
            }
        }
        else
        {
            // Self-kill or world kill — just record the death
            _statistics.RecordKill(0, victimSteamId, victimTeam, false);
        }

        // Handle assist
        if (assister != null && assister.IsValid && !assister.IsBot)
        {
            ulong assisterSteamId = assister.SteamID;
            CsTeam assisterTeam = (CsTeam)assister.TeamNum;
            _statistics.GetOrCreateStats(assisterSteamId, assister.PlayerName, assisterTeam);
            _statistics.RecordAssist(assisterSteamId, @event.Assistedflash);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (!_matchService.IsLive) return HookResult.Continue;

        var attacker = @event.Attacker;
        var victim = @event.Userid;

        if (attacker == null || !attacker.IsValid || attacker.IsBot) return HookResult.Continue;
        if (victim == null || !victim.IsValid || victim.IsBot) return HookResult.Continue;

        // Only count damage to enemies
        if ((CsTeam)attacker.TeamNum != (CsTeam)victim.TeamNum)
        {
            _statistics.GetOrCreateStats(attacker.SteamID, attacker.PlayerName, (CsTeam)attacker.TeamNum);
            _statistics.RecordDamage(attacker.SteamID, @event.DmgHealth);
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundAnnounceMatchStart(EventRoundAnnounceMatchStart @event, GameEventInfo info)
    {
        // Disparado quando a partida realmente vai ao vivo (pós warmup/faca).
        // Diferente de EventRoundStart, que também dispara durante o warmup — usá-lo aqui
        // evita que a contagem inicial de jogadores seja tirada antes dos times serem
        // definidos. Não há early-return por _matchService.IsLive aqui de propósito: se
        // esse evento disparar de novo enquanto já estamos live, o próprio jogo reiniciou
        // o warmup+faca no meio da partida (reload de plugin/modo) — MatchService.SetLive
        // trata esse caso como um restart real e descarta as estatísticas antigas.
        string? currentModeName = ResolveCurrentModeName();

        if (!GameModeGate.IsRankedMode(currentModeName, _config.RankedModeName))
        {
            _logger.LogWarning(
                "[MixRanking] Match start ignorado — modo atual: '{Mode}' (esperado: '{Required}'). Partida não será ranqueada.",
                currentModeName ?? "desconhecido (GameModeManager indisponível ou capability não resolvida)",
                _config.RankedModeName);
            return HookResult.Continue;
        }

        var players = Utilities.GetPlayers();
        int ctCount = 0, tCount = 0;

        foreach (var player in players)
        {
            if (!player.IsValid) continue;

            var team = (CsTeam)player.TeamNum;
            if (PlayerFilters.IsActiveMatchPlayer(player.IsValid, player.IsBot, player.IsHLTV, team))
            {
                if (team == CsTeam.CounterTerrorist) ctCount++;
                else if (team == CsTeam.Terrorist) tCount++;

                // Initialize stats for all players
                _statistics.GetOrCreateStats(player.SteamID, player.PlayerName, team);
            }
        }

        _matchService.SetLive(ctCount, tCount);

        return HookResult.Continue;
    }

    /// <summary>
    /// Resolve o nome do modo atual via a capability game_mode:api do GameModeManager.
    /// Isolado em try/catch porque essa chamada atravessa a fronteira de um plugin externo:
    /// uma versão de GameModeManager desatualizada/incompatível no servidor pode não
    /// implementar membros que o GameModeManager.Shared referenciado aqui espera (ex:
    /// MissingMethodException em IGameModeApi.State), o que sem isso derrubava o handler
    /// inteiro antes de qualquer log — a partida ficava sem nenhum registro e sem pista do motivo.
    /// </summary>
    private string? ResolveCurrentModeName()
    {
        try
        {
            return _gameModeCapability.Get()?.State?.CurrentMode?.Name;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[MixRanking] Falha ao consultar GameModeManager (game_mode:api) — possível incompatibilidade de versão entre o plugin MixRanking e o GameModeManager instalado no servidor. Partida não será ranqueada.");
            return null;
        }
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (!_matchService.IsLive) return HookResult.Continue;

        _matchService.OnRoundStart();
        _statistics.OnRoundStart();

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (!_matchService.IsLive) return HookResult.Continue;

        // Collect alive players
        var aliveSteamIds = new List<ulong>();
        var players = Utilities.GetPlayers();

        foreach (var player in players)
        {
            if (!player.IsValid) continue;

            var team = (CsTeam)player.TeamNum;
            if (PlayerFilters.IsActiveMatchPlayer(player.IsValid, player.IsBot, player.IsHLTV, team))
            {
                var pawn = player.PlayerPawn?.Value;
                if (pawn != null && pawn.Health > 0)
                {
                    aliveSteamIds.Add(player.SteamID);
                }
            }
        }

        _statistics.OnRoundEnd(aliveSteamIds);

        return HookResult.Continue;
    }

    private HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        if (!_matchService.IsLive) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        _statistics.RecordMvp(player.SteamID);

        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        // Reserved for future stats tracking
        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        // Reserved for future stats tracking
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (!_matchService.IsLive) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        // Reason distingue abandono real (saiu, foi kickado, caiu a conexão) de uma
        // desconexão causada pelo próprio servidor (changelevel, troca de modo do
        // GameModeManager, reload de plugin) — essa última não deve penalizar o jogador.
        if (DisconnectReasons.IsInfrastructureTransition(@event.Reason))
        {
            _logger.LogInformation(
                "[MixRanking] Player {Name} ({SteamId}) caiu por transição de infraestrutura do servidor (reason={Reason}) — não contabilizado como abandono.",
                player.PlayerName, player.SteamID, @event.Reason);
            return HookResult.Continue;
        }

        _statistics.MarkAbandoned(player.SteamID);
        _logger.LogInformation("[MixRanking] Player {Name} ({SteamId}) abandoned the match.",
            player.PlayerName, player.SteamID);

        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        if (!_matchService.IsLive) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        // Jogador reconectou antes do fim da partida — reverte qualquer marca de abandono
        // deixada por um disconnect anterior (queda de rede, reload de plugin) para que ele
        // não leve a penalidade de rating por algo que já foi corrigido sozinho.
        if (_statistics.ClearAbandoned(player.SteamID))
        {
            _logger.LogInformation("[MixRanking] Player {Name} ({SteamId}) reconectou — marca de abandono revertida.",
                player.PlayerName, player.SteamID);
        }

        return HookResult.Continue;
    }

    private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        _logger.LogInformation("[MixRanking] EventCsWinPanelMatch received.");

        if (!_matchService.IsLive)
        {
            _logger.LogWarning("[MixRanking] Match end received but match is not live. Skipping.");
            return HookResult.Continue;
        }

        // Announce match end immediately
        Server.PrintToChatAll($" {ChatColors.Gold}[{_config.ChatPrefix}]{ChatColors.Default} Partida finalizada! Processando estatísticas e ranking...");

        // Get scores from team manager entities
        int ctScore = 0;
        int tScore = 0;

        var teamManagers = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
        foreach (var team in teamManagers)
        {
            if (team.TeamNum == (int)CsTeam.CounterTerrorist)
                ctScore = team.Score;
            else if (team.TeamNum == (int)CsTeam.Terrorist)
                tScore = team.Score;
        }

        if (ctScore == 0 && tScore == 0)
        {
            _logger.LogWarning("[MixRanking] Could not determine match scores.");
            return HookResult.Continue;
        }

        CsTeam winnerTeam = ctScore > tScore ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        string mapName = Server.MapName;

        _logger.LogInformation("[MixRanking] Final scores — CT: {CtScore}, T: {TScore}, Winner: {Winner}",
            ctScore, tScore, winnerTeam);

        // Process asynchronously
        Task.Run(async () =>
        {
            try
            {
                var ratingChanges = await _matchService.ProcessMatchEndAsync(mapName, winnerTeam, ctScore, tScore);

                if (ratingChanges != null)
                {
                    // Send results to chat on the main thread
                    Server.NextFrame(() => BroadcastResults(ratingChanges, ctScore, tScore));
                }
                else
                {
                    Server.NextFrame(() =>
                        Server.PrintToChatAll($" {ChatColors.Red}[{_config.ChatPrefix}]{ChatColors.Default} Partida não contabilizada para o ranking."));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{_config.ChatPrefix}] Error processing match end.");
                Server.NextFrame(() =>
                    Server.PrintToChatAll($" {ChatColors.Red}[{_config.ChatPrefix}]{ChatColors.Default} Erro ao salvar resultado da partida, contate um admin."));
            }
            finally
            {
                Server.NextFrame(() => _matchService.Reset());
            }
        });

        return HookResult.Continue;
    }

    /// <summary>Envia resultados de rating no chat.</summary>
    private void BroadcastResults(List<RatingChange> ratingChanges, int ctScore, int tScore)
    {
        Server.PrintToChatAll($" {ChatColors.Gold}🏆 ══════════ Fim de Partida ({ctScore} x {tScore}) ══════════ 🏆");

        // Sort by team, then by total change descending
        var sorted = ratingChanges.OrderByDescending(r => r.Won).ThenByDescending(r => r.TotalChange);

        foreach (var change in sorted)
        {
            string totalText = change.TotalChange >= 0
                ? $"{ChatColors.Green}+{change.TotalChange}"
                : $"{ChatColors.Red}{change.TotalChange}";

            string detailsText = change.PerformanceSwing >= 0
                ? $"{ChatColors.Grey}(Base: {change.BaseChange:+#;-#;0} │ Swing: {ChatColors.Lime}+{change.PerformanceSwing}{ChatColors.Grey})"
                : $"{ChatColors.Grey}(Base: {change.BaseChange:+#;-#;0} │ Swing: {ChatColors.Red}{change.PerformanceSwing}{ChatColors.Grey})";

            Server.PrintToChatAll(
                $" {ChatColors.Gold}» {ChatColors.Default}{change.PlayerName}: {ChatColors.Yellow}{change.NewRating} {ChatColors.Grey}[{totalText}{ChatColors.Grey}] {detailsText}");
        }

        Server.PrintToChatAll($" {ChatColors.Gold}🏆 ═══════════════════════════════════════════ 🏆");
    }
}
