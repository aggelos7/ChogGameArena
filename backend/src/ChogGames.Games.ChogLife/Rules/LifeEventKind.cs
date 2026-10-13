namespace ChogGames.Games.ChogLife.Rules;

/// <summary>
/// The kind of event an <see cref="LifeTileType.Event"/> tile triggers (LIFE-004, DEC-037). Only the kind is
/// configured here; odds and outcomes come with STORY-038.
/// </summary>
public enum LifeEventKind
{
    Market,
    Airdrop,
    Rug,
    Liquidation,
}
