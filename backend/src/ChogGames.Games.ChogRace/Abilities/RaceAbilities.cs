using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;
using ChogGames.Games.ChogRace.Rules;

namespace ChogGames.Games.ChogRace.Abilities;

/// <summary>
/// Chog Race's abilities (RACE-007, STORY-055, DEC-059), one of which each player locks for a match and uses once, on
/// their own turn and never while it waits for an attack's target (DEC-058).
/// <list type="bullet">
/// <item>Speed Boost is used before the roll: that turn's roll moves <c>tiles</c> more tiles (2 at Base), stopping on
/// the Finish, and the tile landed on resolves as usual. A turn that runs out first keeps it for the next roll.</item>
/// <item>Shield is used before or after the roll and stays up, for everyone to see, until it has blocked <c>blocks</c>
/// Rug, Liquidated or Whale cards aimed at its player (1 at Base), each at once, with no reply window and their HODL
/// kept. Reverse isn't blocked.</item>
/// <item>Card Vision is used before or after the roll on a deck with cards: only its player sees the deck's top
/// <c>cards</c> cards (1 at Base) until the next draw, which ends it.</item>
/// <item>Card Steal is used before or after the roll on an opponent holding a card: up to <c>cards</c> of their cards (1
/// at Base), each drawn at random by the server, move to the player's hand while it has room. Neither HODL nor Shield
/// blocks it; only the two of them see which cards moved.</item>
/// </list>
/// The values here are the Base Tier's; holders of rarer Tiers lock stronger ones from <c>Abilities:Holder</c> (STORY-090,
/// DEC-077). Version 2 added <c>blocks</c> and both <c>cards</c>.
/// </summary>
public static class RaceAbilities
{
    public const int Version = 2;

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

    /// <summary>The attacks a Shield blocks before it is spent; 1 to <see cref="MaxBlocks"/>.</summary>
    public const string Blocks = "blocks";

    /// <summary>The top cards Card Vision shows, or the cards Card Steal takes; 1 to <see cref="MaxCards"/>.</summary>
    public const string Cards = "cards";

    public const int MaxBlocks = 5;

    public const int MaxCards = 5;

    /// <summary>The catalog, version 2, whose handlers act on <paramref name="game"/>'s match states.</summary>
    public static AbilityCatalog Catalog(ChogRaceMatchRules game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return AbilityCatalog.Create(
            GameDefinition.ChogRace,
            Version,
            Definitions(),
            [
                new Handler(game, MovementBoost, [new AbilityParameter(Tiles, 1, RaceRules.CardMoveLimit)]),
                new Handler(game, AttackBlock, [new AbilityParameter(Blocks, 1, MaxBlocks, Default: 1)]),
                new Handler(game, CardPreview, [new AbilityParameter(Cards, 1, MaxCards, Default: 1)]),
                new Handler(game, CardSteal, [new AbilityParameter(Cards, 1, MaxCards, Default: 1)]),
            ],
            new Timing(game));
    }

    private static IEnumerable<AbilityDefinition> Definitions() =>
    [
        AbilityDefinition.Create(
            MovementBoost,
            "Speed Boost",
            "Use before you roll. Your roll moves {tiles} more tiles.",
            AbilityTargetKind.None,
            [BeforeRoll],
            new Dictionary<string, decimal> { [Tiles] = 2 }),
        AbilityDefinition.Create(
            AttackBlock,
            "Shield",
            "Use on your turn. Blocks {blocks|the next attack|the next # attacks} played on you (Rug, Liquidated or Whale), and you keep your HODL.",
            AbilityTargetKind.None,
            [BeforeRoll, AfterRoll],
            new Dictionary<string, decimal> { [Blocks] = 1 }),
        AbilityDefinition.Create(
            CardPreview,
            "Card Vision",
            "Use on your turn. Only you see {cards|the top card|the top # cards} of the deck, until someone draws.",
            AbilityTargetKind.None,
            [BeforeRoll, AfterRoll],
            new Dictionary<string, decimal> { [Cards] = 1 }),
        AbilityDefinition.Create(
            CardSteal,
            "Card Steal",
            "Use on your turn. Take {cards|a random card|up to # random cards} from an opponent holding one, while your hand has room.",
            AbilityTargetKind.Opponent,
            [BeforeRoll, AfterRoll],
            new Dictionary<string, decimal> { [Cards] = 1 }),
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
