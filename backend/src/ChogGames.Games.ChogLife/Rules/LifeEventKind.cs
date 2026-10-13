namespace ChogGames.Games.ChogLife.Rules;

/// <summary>
/// The kind of event an <see cref="LifeTileType.Event"/> tile triggers (LIFE-004, DEC-037). Its odds and outcomes
/// are the rules' <see cref="LifeEvent"/> of that kind (STORY-038, DEC-040).
/// </summary>
public enum LifeEventKind
{
    Market,
    Airdrop,
    Rug,
    Liquidation,
}
