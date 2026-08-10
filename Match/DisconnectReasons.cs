using CounterStrikeSharp.API.ValveConstants.Protobuf;

namespace MixRanking.Match;

/// <summary>
/// Classifica o motivo (EventPlayerDisconnect.Reason) de uma desconexão para distinguir
/// abandono real (jogador saiu/foi kickado/caiu a conexão) de uma transição de
/// infraestrutura do servidor (changelevel, troca de modo do GameModeManager, reload de
/// plugin) que derruba a conexão de todo mundo momentaneamente sem ninguém ter saído.
///
/// Motivo concreto: em 2026-08-08, uma troca de modo do GameModeManager (Competitive -> Competitive,
/// via voto) recarregou MatchZy/CS2Rcon/DamageManagement no meio de uma partida ao vivo e
/// derrubou ~10 dos 15 jogadores em menos de 1 segundo, todos com Reason=55
/// (NETWORK_DISCONNECT_LOOPDEACTIVATE). Nenhum deles realmente saiu — a partida seguiu e
/// terminou normalmente 55 minutos depois — mas ficaram marcados Abandoned=true e levaram
/// a penalidade de rating do resto da vida da partida.
/// </summary>
public static class DisconnectReasons
{
    /// <summary>
    /// Motivos de NetworkDisconnectionReason causados pelo servidor/engine (changelevel,
    /// troca de modo, shutdown de plugin) e não por uma ação do jogador ou da rede dele.
    /// </summary>
    private static readonly HashSet<NetworkDisconnectionReason> InfrastructureReasons = new()
    {
        NetworkDisconnectionReason.NETWORK_DISCONNECT_SHUTDOWN,
        NetworkDisconnectionReason.NETWORK_DISCONNECT_RECONNECTION,
        NetworkDisconnectionReason.NETWORK_DISCONNECT_LOOPSHUTDOWN,
        NetworkDisconnectionReason.NETWORK_DISCONNECT_LOOPDEACTIVATE,
        NetworkDisconnectionReason.NETWORK_DISCONNECT_HOST_ENDGAME,
        NetworkDisconnectionReason.NETWORK_DISCONNECT_LOOP_LEVELLOAD_ACTIVATE,
        NetworkDisconnectionReason.NETWORK_DISCONNECT_CREATE_SERVER_FAILED,
        NetworkDisconnectionReason.NETWORK_DISCONNECT_EXITING,
        NetworkDisconnectionReason.NETWORK_DISCONNECT_SERVER_SHUTDOWN,
    };

    /// <summary>
    /// True quando o disconnect foi causado por uma transição de infraestrutura do servidor
    /// (não deve contar como abandono do jogador).
    /// </summary>
    public static bool IsInfrastructureTransition(int reason) =>
        InfrastructureReasons.Contains((NetworkDisconnectionReason)reason);
}
