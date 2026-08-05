namespace MixRanking.Database;

public partial class SupabaseDatabaseService
{
    public async Task SetPlayerRatingWithAuditAsync(string targetSteamId, int newRating, string? adminSteamId, string adminName, string? reason)
    {
        using var response = await SendAsync(HttpMethod.Post, "rpc/set_player_rating", new
        {
            p_target_steamid = targetSteamId,
            p_new_rating = newRating,
            p_admin_steamid = adminSteamId,
            p_admin_name = adminName,
            p_reason = reason
        });
    }

    public async Task ResetPlayerWithAuditAsync(string targetSteamId, int initialRating, string? adminSteamId, string adminName, string? reason)
    {
        using var response = await SendAsync(HttpMethod.Post, "rpc/reset_player", new
        {
            p_target_steamid = targetSteamId,
            p_initial_rating = initialRating,
            p_admin_steamid = adminSteamId,
            p_admin_name = adminName,
            p_reason = reason
        });
    }

    public async Task AdjustPlayerRatingWithAuditAsync(string targetSteamId, int amount, bool isAdd, int minRating, string? adminSteamId, string adminName, string? reason)
    {
        using var response = await SendAsync(HttpMethod.Post, "rpc/adjust_player_rating", new
        {
            p_target_steamid = targetSteamId,
            p_amount = amount,
            p_is_add = isAdd,
            p_min_rating = minRating,
            p_admin_steamid = adminSteamId,
            p_admin_name = adminName,
            p_reason = reason
        });
    }

    public async Task ResetAllDataWithAuditAsync(string? adminSteamId, string adminName, string? reason)
    {
        using var response = await SendAsync(HttpMethod.Post, "rpc/reset_all_data", new
        {
            p_admin_steamid = adminSteamId,
            p_admin_name = adminName,
            p_reason = reason
        });
    }
}
