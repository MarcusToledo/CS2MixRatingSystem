using MixRanking.Models;

namespace MixRanking.Rating;

/// <summary>
/// Calcula o swing de performance individual.
/// O swing nunca define sozinho a pontuação — apenas modifica o ganho/perda.
/// Limite: -maxSwing a +maxSwing.
/// </summary>
public static class SwingCalculator
{
    // Pesos das métricas
    private const double AdrWeight = 0.40;
    private const double KastWeight = 0.25;
    private const double KprWeight = 0.20;
    private const double KdWeight = 0.10;
    private const double MvpWeight = 0.05;

    /// <summary>
    /// Calcula o swing de performance de um jogador.
    /// </summary>
    /// <param name="stats">Estatísticas da partida do jogador.</param>
    /// <param name="maxSwing">Swing máximo permitido (ex: 7).</param>
    /// <returns>Valor do swing entre -maxSwing e +maxSwing.</returns>
    public static int CalculateSwing(MatchPlayerStats stats, int maxSwing)
    {
        if (stats.RoundsPlayed <= 0)
            return 0;

        // 1. ADR Score (40%)
        // Benchmark: ADR 80 = average (0.5), ADR 120+ = excellent (1.0)
        double adr = stats.Adr;
        double adrScore = NormalizeValue(adr, 40.0, 120.0);

        // 2. KAST Score (25%)
        // KAST = % of rounds with Kill, Assist, Survived, or Traded
        // Using RoundsWithKill + assists contribution + survived
        double kastRounds = stats.RoundsWithKill + (stats.Assists * 0.5) + stats.RoundsSurvived + stats.TradeKills;
        // Avoid double counting: cap at rounds played
        double kastPercent = Math.Min(kastRounds / stats.RoundsPlayed, 1.0);
        // Benchmark: 70% KAST = average (0.5), 90%+ = excellent (1.0)
        double kastScore = NormalizeValue(kastPercent * 100.0, 50.0, 90.0);

        // 3. Kills per Round Score (20%)
        // Benchmark: 0.7 KPR = average (0.5), 1.2+ KPR = excellent (1.0)
        double kpr = (double)stats.Kills / stats.RoundsPlayed;
        double kprScore = NormalizeValue(kpr, 0.3, 1.2);

        // 4. K/D Score (10%)
        // Benchmark: 1.0 KD = average (0.5), 2.0+ KD = excellent (1.0)
        double kd = stats.KdRatio;
        double kdScore = NormalizeValue(kd, 0.5, 2.0);

        // 5. MVP Score (5%)
        // Benchmark: normalize based on rounds played
        double mvpPerRound = (double)stats.Mvps / stats.RoundsPlayed;
        double mvpScore = NormalizeValue(mvpPerRound, 0.0, 0.3);

        // Weighted performance score (0.0 to 1.0)
        double performanceScore = 
            (adrScore * AdrWeight) +
            (kastScore * KastWeight) +
            (kprScore * KprWeight) +
            (kdScore * KdWeight) +
            (mvpScore * MvpWeight);

        // Map from [0.0, 1.0] to [-maxSwing, +maxSwing]
        // A performance score of 0.5 (average) maps to swing 0
        double swing = (performanceScore - 0.5) * 2.0 * maxSwing;

        // Clamp to [-maxSwing, +maxSwing]
        swing = Math.Clamp(swing, -maxSwing, maxSwing);

        return (int)Math.Round(swing);
    }

    /// <summary>
    /// Normaliza um valor para o range [0.0, 1.0].
    /// </summary>
    /// <param name="value">Valor atual.</param>
    /// <param name="min">Valor mínimo do range (mapeia para 0.0).</param>
    /// <param name="max">Valor máximo do range (mapeia para 1.0).</param>
    private static double NormalizeValue(double value, double min, double max)
    {
        if (max <= min) return 0.5;
        return Math.Clamp((value - min) / (max - min), 0.0, 1.0);
    }
}
