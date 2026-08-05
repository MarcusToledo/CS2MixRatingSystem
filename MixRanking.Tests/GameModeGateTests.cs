using MixRanking.Match;
using Xunit;

namespace MixRanking.Tests;

public class GameModeGateTests
{
    [Fact]
    public void IsRankedMode_ReturnsTrue_WhenCurrentModeMatchesRequired()
    {
        bool result = GameModeGate.IsRankedMode(currentModeName: "Competitivo", requiredModeName: "Competitivo");

        Assert.True(result);
    }

    [Fact]
    public void IsRankedMode_IsCaseInsensitive()
    {
        bool result = GameModeGate.IsRankedMode(currentModeName: "competitivo", requiredModeName: "Competitivo");

        Assert.True(result);
    }

    [Fact]
    public void IsRankedMode_ReturnsFalse_WhenCurrentModeIsRetake()
    {
        // GameModeManager troca o modo (ex: "Retake") via !modes; só "Competitivo" deve ranquear.
        bool result = GameModeGate.IsRankedMode(currentModeName: "Retake", requiredModeName: "Competitivo");

        Assert.False(result);
    }

    [Fact]
    public void IsRankedMode_ReturnsFalse_WhenCurrentModeIsNull()
    {
        // Fail-closed: capability do GameModeManager indisponível ou modo não resolvido não deve ranquear.
        bool result = GameModeGate.IsRankedMode(currentModeName: null, requiredModeName: "Competitivo");

        Assert.False(result);
    }
}
