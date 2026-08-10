using MixRanking.Match;
using Xunit;

namespace MixRanking.Tests;

public class DisconnectReasonsTests
{
    // 55 = NETWORK_DISCONNECT_LOOPDEACTIVATE — o motivo real observado em 2026-08-08 quando
    // uma troca de modo do GameModeManager derrubou ~10 jogadores de uma vez no meio de uma
    // partida ao vivo, sem que nenhum deles tivesse realmente saído.
    [Theory]
    [InlineData(1)]  // NETWORK_DISCONNECT_SHUTDOWN
    [InlineData(53)] // NETWORK_DISCONNECT_RECONNECTION
    [InlineData(54)] // NETWORK_DISCONNECT_LOOPSHUTDOWN
    [InlineData(55)] // NETWORK_DISCONNECT_LOOPDEACTIVATE
    [InlineData(56)] // NETWORK_DISCONNECT_HOST_ENDGAME
    [InlineData(57)] // NETWORK_DISCONNECT_LOOP_LEVELLOAD_ACTIVATE
    [InlineData(58)] // NETWORK_DISCONNECT_CREATE_SERVER_FAILED
    [InlineData(59)] // NETWORK_DISCONNECT_EXITING
    [InlineData(69)] // NETWORK_DISCONNECT_SERVER_SHUTDOWN
    public void IsInfrastructureTransition_ReturnsTrue_ForServerDrivenReasons(int reason)
    {
        Assert.True(DisconnectReasons.IsInfrastructureTransition(reason));
    }

    [Theory]
    [InlineData(2)]   // NETWORK_DISCONNECT_DISCONNECT_BY_USER
    [InlineData(4)]   // NETWORK_DISCONNECT_LOST
    [InlineData(29)]  // NETWORK_DISCONNECT_TIMEDOUT
    [InlineData(39)]  // NETWORK_DISCONNECT_KICKED
    [InlineData(79)]  // NETWORK_DISCONNECT_REMOTE_TIMEOUT
    [InlineData(158)] // NETWORK_DISCONNECT_KICKED_IDLE
    public void IsInfrastructureTransition_ReturnsFalse_ForRealAbandonReasons(int reason)
    {
        Assert.False(DisconnectReasons.IsInfrastructureTransition(reason));
    }
}
