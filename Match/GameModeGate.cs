namespace MixRanking.Match;

/// <summary>
/// Decide se o modo de jogo atual (reportado pelo GameModeManager) deve ser ranqueado.
/// </summary>
public static class GameModeGate
{
    public static bool IsRankedMode(string? currentModeName, string requiredModeName)
        => currentModeName != null &&
           string.Equals(currentModeName, requiredModeName, StringComparison.OrdinalIgnoreCase);
}
