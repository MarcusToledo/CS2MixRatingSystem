namespace MixRanking.Models;

/// <summary>
/// Contém as informações de estatísticas e rating de um jogador ao fim da partida
/// para serem salvas na mesma transação.
/// </summary>
public class MatchPlayerUpdate
{
    public required MatchPlayerStats Stats { get; set; }
    public required PlayerData PlayerData { get; set; }
    public required RatingChange RatingChange { get; set; }
    public required int NewRating { get; set; }
    public required bool Won { get; set; }
}
