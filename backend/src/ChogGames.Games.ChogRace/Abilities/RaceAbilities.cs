using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;
using ChogGames.Games.ChogRace.Rules;

namespace ChogGames.Games.ChogRace.Abilities;

/// <summary>
/// Chog Race's abilities (RACE-007, STORY-055, DEC-059), one of which each player locks for a match and uses once, on
/// their own turn and never while it waits for an attack's target (DEC-058).
/// <list type="bullet">
/// <item>Speed Boost is used before the roll: that turn's roll moves 2 more tiles, stopping on the Finish, and the tile
/// landed on resolves as usual. A turn that runs out first keeps it for the next roll.</item>
/// <item>Shield is used before or after the roll and stays up, for everyone to see, until the next Rug, Liquidated or
/// Whale aimed at its player, which it blocks at once, with no reply window and their HODL kept. Reverse isn't
/// blocked.</item>
/// <item>Card Vision is used before or after the roll on a deck with cards: only its player sees the deck's top card,
/// for as long as it stays on top; any draw ends it.</item>
/// <item>Card Steal is used before or after the roll on an opponent holding a card: one of their cards, drawn at random
/// by the server, moves to the player's hand, which must have room. Neither HODL nor Shield blocks it; only the two of
/// them see which card moved.</item>
/// </list>
/// These are proposed defaults to balance later (requirements §15); a change to any definition means catalog version 2.
/// </summary>
public static class RaceAbilities
{
    public const int Version = 1;

    public const string MovementBoost = "movement_boost";
    public const string AttackBlock = "attack_block";
    public const string CardPreview = "card_preview";
    public const string CardSteal = "card_steal";

    /// <summary>The player's turn, in the Move phase, before they roll.</summary>
    public const string BeforeRoll = "before_roll";

    /// <summary>The player's turn, in the Play phase, after they rolled.</summary>
    public const string AfterRoll = "after_roll";

    /// <summary>The tiles Speed Boost adds to the roll; a whole number of 1 to <see cref="RaceRules.CardMoveLimit"/>.</summary>
    public const string Tiles = "tiles";

    /// <summary>The catalog, version 1, whose handlers act on <paramref name="game"/>'s match states.</summary>
    public static AbilityCatalog Catalog(ChogRaceMatchRules game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return AbilityCatalog.Create(
            GameDefinition.ChogRace,
            Version,
            Definitions(),
            [
                new Handler(game, MovementBoost, [new AbilityParameter(Tiles, 1, RaceRules.CardMoveLimit)]),
                new Handler(game, AttackBlock, []),
                new Handler(game, CardPreview, []),
                new Handler(game, CardSteal, []),
            ],
            new Timing(game));
    }

    private static IEnumerable<AbilityDefinition> Definitions() =>
    [
        AbilityDefinition.Create(
            MovementBoost,
            "Speed Boost",
            "Use before you roll. Your roll moves 2 more tiles.",
            AbilityTargetKind.None,
            [BeforeRoll],
            new Dictionary<string, decimal> { [Tiles] = 2 }),
        AbilityDefinition.Create(
            AttackBlock,
            "Shield",
            "Use on your turn. The next Rug, Liquidated or Whale played on you is blocked, and you keep your HODL.",
            AbilityTargetKind.None,
            [BeforeRoll, AfterRoll]),
        AbilityDefinition.Create(
            CardPreview,
            "Card Vision",
            "Use on your turn. Only you see the next card in the deck, until someone draws it.",
            AbilityTargetKind.None,
            [BeforeRoll, AfterRoll]),
        AbilityDefinition.Create(
            CardSteal,
            "Card Steal",
            "Use on your turn. Take a random card from an opponent holding one, if your hand has room.",
            AbilityTargetKind.Opponent,
            [BeforeRoll, AfterRoll]),
    ];

    private sealed class Handler(ChogRaceMatchRules game, string abilityId, IReadOnlyList<AbilityParameter> parameters) : IAbilityHandler
    {
        public string AbilityId => abilityId;

        public IReadOnlyList<AbilityParameter> Parameters => parameters;

        public string? CanExecute(AbilityContext context) => game.CanUse(context);

        public JsonElement Execute(AbilityContext context) => game.Use(context);
    }

    private sealed class Timing(ChogRaceMatchRules game) : IAbilityTiming
    {
        public string? CurrentWindow(AbilityContext context) => game.AbilityWindow(context);
    }
}
