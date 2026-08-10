using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using MixRanking.Config;
using MixRanking.Models;
using MixRanking.Services;
using Microsoft.Extensions.Logging;

namespace MixRanking.Match;

/// <summary>
/// Gerencia o estado da partida e orquestra o processamento ao final.
/// Recebe eventos, não calcula rating diretamente.
/// </summary>
public class MatchService
{
    private readonly StatisticsService _statistics;
    private readonly RatingService _ratingService;
    private readonly RankingConfig _config;
    private readonly ILogger _logger;

    /// <summary>Se a partida está ativa (live).</summary>
    public bool IsLive { get; private set; }

    /// <summary>Round atual.</summary>
    public int CurrentRound { get; private set; }

    /// <summary>GUID único da partida atual.</summary>
    public string MatchGuid { get; private set; } = string.Empty;

    /// <summary>Número de jogadores CT no início.</summary>
    public int InitialCtCount { get; private set; }

    /// <summary>Número de jogadores T no início.</summary>
    public int InitialTCount { get; private set; }

    /// <summary>Se o processamento do fim da partida já foi feito.</summary>
    private bool _matchProcessed;

    public MatchService(StatisticsService statistics, RatingService ratingService,
        RankingConfig config, ILogger logger)
    {
        _statistics = statistics;
        _ratingService = ratingService;
        _config = config;
        _logger = logger;
    }

    /// <summary>Inicia uma nova partida.</summary>
    public void StartMatch()
    {
        _statistics.Reset();
        IsLive = false;
        CurrentRound = 0;
        MatchGuid = Guid.NewGuid().ToString();
        _matchProcessed = false;
        InitialCtCount = 0;
        InitialTCount = 0;

        _logger.LogInformation("[MixRanking] New match initialized: {MatchGuid}", MatchGuid);
    }

    /// <summary>
    /// Marca a partida como live (após warmup/knife). Em condições normais isso só
    /// acontece uma vez por partida — EventRoundAnnounceMatchStart é o sinal do próprio
    /// jogo para "warmup/faca terminou, a partida está ao vivo agora". Se ele disparar de
    /// novo enquanto já estamos live, é porque algo externo (reload do MatchZy, restart de
    /// mapa/modo) reiniciou o warmup+faca no meio da partida — as estatísticas acumuladas
    /// até aqui incluem rounds que não pertencem à partida ranqueada final e precisam ser
    /// descartadas, senão inflam RoundsPlayed/Kills/ADR de todo mundo (ex: partida real de
    /// 21 rounds registrando RoundsPlayed=56 por causa de um reload no meio do jogo).
    /// </summary>
    public void SetLive(int ctCount, int tCount)
    {
        if (IsLive)
        {
            _logger.LogWarning(
                "[MixRanking] Partida reiniciada externamente no meio do jogo (round {Round}) — " +
                "descartando estatísticas acumuladas antes do restart.", CurrentRound);
            _statistics.Reset();
            CurrentRound = 0;
        }

        IsLive = true;
        InitialCtCount = ctCount;
        InitialTCount = tCount;
        _logger.LogInformation("[MixRanking] Match is now LIVE. CT: {CtCount}, T: {TCount}", ctCount, tCount);
    }

    /// <summary>Incrementa o round.</summary>
    public void OnRoundStart()
    {
        CurrentRound++;
    }

    /// <summary>
    /// Processa o fim da partida.
    /// Retorna null se a partida não for válida para ranking.
    /// </summary>
    public async Task<List<RatingChange>?> ProcessMatchEndAsync(string map, CsTeam winnerTeam, int ctScore, int tScore)
    {
        if (_matchProcessed)
        {
            _logger.LogWarning("[MixRanking] Match already processed, skipping.");
            return null;
        }

        _matchProcessed = true;
        IsLive = false;

        int totalRounds = ctScore + tScore;

        _logger.LogInformation(
            "[MixRanking] Match ended. Map: {Map}, CT: {CtScore}, T: {TScore}, Rounds: {TotalRounds}, Winner: {Winner}",
            map, ctScore, tScore, totalRounds, winnerTeam);

        // Validate match
        if (totalRounds < _config.MinRounds)
        {
            _logger.LogWarning("[MixRanking] Match not counted: only {Rounds} rounds (min: {Min}).", totalRounds, _config.MinRounds);
            return null;
        }

        if (InitialCtCount < _config.MinPlayersPerTeam || InitialTCount < _config.MinPlayersPerTeam)
        {
            _logger.LogWarning("[MixRanking] Match not counted: CT={Ct}, T={T} (min: {Min} per team).",
                InitialCtCount, InitialTCount, _config.MinPlayersPerTeam);
            return null;
        }

        if (winnerTeam != CsTeam.Terrorist && winnerTeam != CsTeam.CounterTerrorist)
        {
            _logger.LogWarning("[MixRanking] Match not counted: no valid winner team.");
            return null;
        }

        // Process rating changes
        var playerStats = _statistics.GetAllPlayerStats();
        var ratingChanges = await _ratingService.ProcessMatchEndAsync(
            MatchGuid, map, winnerTeam, ctScore, tScore, playerStats);

        _logger.LogInformation("[MixRanking] Processed {Count} player ratings.", ratingChanges.Count);

        return ratingChanges;
    }

    /// <summary>Reseta o estado da partida.</summary>
    public void Reset()
    {
        _statistics.Reset();
        IsLive = false;
        CurrentRound = 0;
        _matchProcessed = false;
    }
}
