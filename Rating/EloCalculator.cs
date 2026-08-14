namespace MixRanking.Rating;

/// <summary>Calculadora de Elo puro baseada no resultado da partida.</summary>
public static class EloCalculator
{
    /// <summary>
    /// Calcula a mudança base de rating para um jogador.
    /// </summary>
    /// <param name="teamAvgRating">Rating médio do time do jogador.</param>
    /// <param name="opponentAvgRating">Rating médio do time adversário.</param>
    /// <param name="won">Se o jogador venceu.</param>
    /// <param name="kFactor">Fator K do Elo (ex: 50).</param>
    /// <returns>Mudança de rating (positiva para ganho, negativa para perda).</returns>
    public static int CalculateBaseChange(double teamAvgRating, double opponentAvgRating, bool won, int kFactor)
    {
        // Expected score based on Elo formula
        double expectedScore = 1.0 / (1.0 + Math.Pow(10.0, (opponentAvgRating - teamAvgRating) / 400.0));

        // Actual result: 1.0 for win, 0.0 for loss
        double actualResult = won ? 1.0 : 0.0;

        // Base change = K * (actual - expected)
        double change = kFactor * (actualResult - expectedScore);

        return (int)Math.Round(change);
    }

    /// <summary>
    /// Garante que a mudança total (BaseChange + Swing) nunca fique abaixo do mínimo
    /// configurado para o resultado da partida: positiva o suficiente em vitória,
    /// negativa o suficiente em derrota. O ajuste é sempre absorvido pelo BaseChange
    /// (nunca pelo swing), preservando o range documentado do swing e a consistência
    /// BaseChange + PerformanceSwing == TotalChange.
    /// </summary>
    /// <param name="baseChange">Mudança base já calculada por CalculateBaseChange.</param>
    /// <param name="swing">Swing de performance (já incluindo penalidade de abandono, se houver).</param>
    /// <param name="won">Se o jogador venceu.</param>
    /// <param name="minMagnitude">Magnitude mínima garantida (config MinRatingChangeMagnitude). 0 desativa.</param>
    /// <returns>BaseChange ajustado (igual ao original se nenhum ajuste for necessário).</returns>
    public static int ApplyMinimumChangeGuarantee(int baseChange, int swing, bool won, int minMagnitude)
    {
        if (minMagnitude <= 0) return baseChange;

        int totalChange = baseChange + swing;

        if (won && totalChange < minMagnitude)
            return baseChange + (minMagnitude - totalChange);

        if (!won && totalChange > -minMagnitude)
            return baseChange - (totalChange + minMagnitude);

        return baseChange;
    }
}
