using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using MixRanking.Commands;
using MixRanking.Config;
using MixRanking.Database;
using MixRanking.Match;
using MixRanking.Services;

namespace MixRanking;

/// <summary>
/// MixRanking — Plugin de ranking permanente para CS2.
/// Sistema Elo com swing de performance, integrado ao MatchZy.
/// </summary>
public class MixRankingPlugin : BasePlugin, IPluginConfig<RankingConfig>
{
    public override string ModuleName => "MixRanking";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "MixRanking";
    public override string ModuleDescription => "Sistema de ranking permanente baseado em Elo com swing de performance.";

    /// <summary>Configuração do plugin (carregada automaticamente do JSON).</summary>
    public RankingConfig Config { get; set; } = new();

    private DatabaseService _database = null!;
    private StatisticsService _statistics = null!;
    private HttpWebSyncClient _webSyncClient = null!;
    private WebSyncService _webSyncService = null!;
    private RatingService _ratingService = null!;
    private PlayerService _playerService = null!;
    private MatchService _matchService = null!;
    private MatchEvents _matchEvents = null!;

    public void OnConfigParsed(RankingConfig config)
    {
        Config = config;
        Logger.LogInformation("[MixRanking] Config loaded — InitialRating: {Rating}, KFactor: {K}, MaxSwing: {Swing}",
            config.InitialRating, config.KFactor, config.MaxSwing);
    }

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("[MixRanking] Loading plugin v{Version}...", ModuleVersion);

        // 1. Initialize database
        string dbPath = Path.Combine(ModuleDirectory, "mixranking.db");
        _database = new DatabaseService(dbPath);

        try
        {
            _database.InitializeAsync().GetAwaiter().GetResult();
            Logger.LogInformation("[MixRanking] Database initialized at {Path}", dbPath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[MixRanking] Failed to initialize database!");
            throw;
        }

        // 2. Initialize services
        _statistics = new StatisticsService();
        _webSyncClient = new HttpWebSyncClient(Config);
        _webSyncService = new WebSyncService(_database, _webSyncClient, Logger);
        _ratingService = new RatingService(_database, Config, _webSyncService);
        _playerService = new PlayerService(_database, Config.InitialRating);
        _matchService = new MatchService(_statistics, _ratingService, Config, Logger);

        // 3. Register event handlers
        _matchEvents = new MatchEvents(_matchService, _statistics, Config, Logger);
        _matchEvents.RegisterEvents(this);

        // 4. Register commands
        var rankCommand = new RankCommand(_playerService, Config);
        rankCommand.Register(this);

        var topCommand = new TopCommand(_playerService, Config);
        topCommand.Register(this);

        var statsCommand = new StatsCommand(_playerService, Config);
        statsCommand.Register(this);

        var adminCommands = new AdminCommands(_database, Config, _webSyncService);
        adminCommands.Register(this);

        // 5. Initialize match on load (for hot reload support)
        _matchService.StartMatch();

        Logger.LogInformation("[MixRanking] Plugin loaded successfully!");
        Logger.LogInformation("[MixRanking] Commands: !rank, !top, !stats, !lastmatch, !profile");
        Logger.LogInformation("[MixRanking] Admin: !rating_set, !rating_reset, !rating_add, !rating_remove");
    }

    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("[MixRanking] Plugin unloaded.");
    }
}
