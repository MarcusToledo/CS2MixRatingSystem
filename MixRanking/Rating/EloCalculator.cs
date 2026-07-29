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
}
