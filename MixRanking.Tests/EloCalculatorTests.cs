using MixRanking.Rating;
using Xunit;

namespace MixRanking.Tests;

public class EloCalculatorTests
{
    [Fact]
    public void ApplyMinimumChangeGuarantee_ReturnsUnchangedBaseChange_WhenTotalAlreadyMeetsMinimum()
    {
        int result = EloCalculator.ApplyMinimumChangeGuarantee(baseChange: 25, swing: 5, won: true, minMagnitude: 3);

        Assert.Equal(25, result);
    }

    [Fact]
    public void ApplyMinimumChangeGuarantee_BoostsBaseChange_WhenWinTotalBelowMinimum()
    {
        // Reproduz o bug relatado: favorito extremo vence com performance ruim,
        // baseChange (5) + swing (-5) resultaria em 0 pontos apesar da vitória.
        int result = EloCalculator.ApplyMinimumChangeGuarantee(baseChange: 5, swing: -5, won: true, minMagnitude: 3);

        Assert.Equal(8, result);
        Assert.Equal(3, result + -5); // total final = minMagnitude
    }

    [Fact]
    public void ApplyMinimumChangeGuarantee_ReducesBaseChange_WhenLossTotalAboveNegativeMinimum()
    {
        // Cenário simétrico: underdog extremo perde com ótima performance,
        // baseChange (-5) + swing (7) resultaria em +2 apesar da derrota.
        int result = EloCalculator.ApplyMinimumChangeGuarantee(baseChange: -5, swing: 7, won: false, minMagnitude: 3);

        Assert.Equal(-10, result);
        Assert.Equal(-3, result + 7); // total final = -minMagnitude
    }

    [Fact]
    public void ApplyMinimumChangeGuarantee_ReturnsUnchanged_WhenMinMagnitudeIsZero()
    {
        int result = EloCalculator.ApplyMinimumChangeGuarantee(baseChange: 5, swing: -5, won: true, minMagnitude: 0);

        Assert.Equal(5, result);
    }

    [Fact]
    public void ApplyMinimumChangeGuarantee_DoesNotAffectAlreadyLargeLosses()
    {
        int result = EloCalculator.ApplyMinimumChangeGuarantee(baseChange: -40, swing: -5, won: false, minMagnitude: 3);

        Assert.Equal(-40, result);
    }
}
