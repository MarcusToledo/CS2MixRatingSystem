using CounterStrikeSharp.API.Modules.Utils;

namespace MixRanking.Match;

/// <summary>
/// Decide se um jogador conectado deve ser tratado como participante ativo da partida
/// (exclui bots, HLTV e espectadores).
/// </summary>
public static class PlayerFilters
{
    public static bool IsActiveMatchPlayer(bool isValid, bool isBot, bool isHltv, CsTeam team)
        => isValid && !isBot && !isHltv &&
           (team == CsTeam.CounterTerrorist || team == CsTeam.Terrorist);
}
