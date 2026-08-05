using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MixRanking.Config;

namespace MixRanking.Database;

/// <summary>Persistência via PostgREST (Supabase), dedicado a este plugin. Ver docs/superpowers/specs/2026-08-02-supabase-persistence-migration-design.md.</summary>
public partial class SupabaseDatabaseService : IDatabaseService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly RankingConfig _config;
    private readonly ILogger _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SupabaseDatabaseService(RankingConfig config, ILogger logger)
        : this(config, logger, new HttpClientHandler())
    {
    }

    /// <summary>Construtor para injeção de um HttpMessageHandler fake em teste.</summary>
    public SupabaseDatabaseService(RankingConfig config, ILogger logger, HttpMessageHandler handler)
    {
        _config = config;
        _logger = logger;
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<bool> InitializeAsync()
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "players?limit=1", null);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MixRanking] Failed to reach Supabase at startup — plugin will keep loading, commands will report errors until connectivity is restored.");
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathAndQuery, object? body, Dictionary<string, string>? extraHeaders = null)
    {
        using var request = new HttpRequestMessage(method, $"{_config.SupabaseUrl}/rest/v1/{pathAndQuery}");
        request.Headers.Add("apikey", _config.SupabaseServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.SupabaseServiceKey);
        if (extraHeaders != null)
        {
            foreach (var (key, value) in extraHeaders) request.Headers.Add(key, value);
        }
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            var errorBody = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new HttpRequestException(
                $"Supabase request failed: {method} {pathAndQuery} -> {(int)statusCode} {statusCode}. Response body: {errorBody}");
        }
        return response;
    }

    public void Dispose() => _httpClient.Dispose();
}
