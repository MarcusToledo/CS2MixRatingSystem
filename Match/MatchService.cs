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

    /// <summary>Marca a partida como live (após warmup/knife).</summary>
    public void SetLive(int ctCount, int tCount)
    {
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
    /// Marca a partida como não mais live, de forma síncrona e imediata (thread principal),
    /// assim que o resultado é conhecido — antes de qualquer processamento assíncrono.
    /// Deve ser chamado no handler do evento de fim de partida, antes de despachar o
    /// processamento assíncrono (Task.Run). Sem isso, uma desconexão que ocorre logo após
    /// o painel de vitória (comportamento normal do jogador, não abandono) pode ser
    /// processada pela thread principal antes do processamento assíncrono chegar a marcar
    /// IsLive como false, e o jogador acaba incorretamente marcado como Abandoned.
    /// </summary>
    public void MarkEnded()
    {
        IsLive = false;
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
