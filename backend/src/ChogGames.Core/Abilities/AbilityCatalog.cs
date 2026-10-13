using ChogGames.Core.Games;

namespace ChogGames.Core.Abilities;

/// <summary>
/// The abilities one game offers, with their handlers and the game's activation windows (STORY-030, DEC-032). It is
/// versioned code in the game module: a change to any definition bumps <see cref="Version"/>, which matches snapshot
/// at start (NFT-015, ADR-008). Everything is checked when the catalog is built, so a malformed catalog stops the API
/// at startup instead of failing during a match.
/// </summary>
public sealed class AbilityCatalog
{
    private readonly Dictionary<string, (AbilityDefinition Definition, IAbilityHandler Handler)> _abilities;

    private AbilityCatalog(
        string gameType,
        int version,
        IReadOnlyList<AbilityDefinition> definitions,
        Dictionary<string, (AbilityDefinition, IAbilityHandler)> abilities,
        IAbilityTiming timing)
    {
        GameType = gameType;
        Version = version;
        Definitions = definitions;
        _abilities = abilities;
        Timing = timing;
    }

    /// <summary>The <see cref="GameDefinition.Id"/> these abilities belong to.</summary>
    public string GameType { get; }

    /// <summary>The catalog's configuration version, from 1.</summary>
    public int Version { get; }

    /// <summary>Every ability of the game, in the order given: the pool players choose from.</summary>
    public IReadOnlyList<AbilityDefinition> Definitions { get; }

    public IAbilityTiming Timing { get; }

    public bool Contains(string abilityId) => _abilities.ContainsKey(abilityId);

    public bool TryFind(string abilityId, out AbilityDefinition definition, out IAbilityHandler handler)
    {
        if (_abilities.TryGetValue(abilityId, out var entry))
        {
            (definition, handler) = entry;
            return true;
        }

        (definition, handler) = (null!, null!);
        return false;
    }

    /// <summary>
    /// A catalog of <paramref name="definitions"/>, each implemented by exactly one of <paramref name="handlers"/> and
    /// configuring exactly the parameters its handler declares, within range. Throws <see cref="ArgumentException"/>
    /// otherwise.
    /// </summary>
    public static AbilityCatalog Create(
        string gameType,
        int version,
        IEnumerable<AbilityDefinition> definitions,
        IEnumerable<IAbilityHandler> handlers,
        IAbilityTiming timing)
    {
        if (string.IsNullOrEmpty(gameType) || gameType.Length > GameDefinition.MaxIdLength)
        {
            throw new ArgumentException($"A game id must have 1 to {GameDefinition.MaxIdLength} characters.", nameof(gameType));
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "A catalog version starts at 1.");
        }

        ArgumentNullException.ThrowIfNull(timing);
        var definitionList = definitions.ToArray();
        if (definitionList.Length == 0)
        {
            throw new ArgumentException($"The {gameType} catalog has no abilities.", nameof(definitions));
        }

        var handlersById = new Dictionary<string, IAbilityHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (!handlersById.TryAdd(handler.AbilityId, handler))
            {
                throw new ArgumentException($"The {gameType} catalog has two handlers for {handler.AbilityId}.", nameof(handlers));
            }
        }

        var abilities = new Dictionary<string, (AbilityDefinition, IAbilityHandler)>(StringComparer.Ordinal);
        foreach (var definition in definitionList)
        {
            if (!handlersById.TryGetValue(definition.Id, out var handler))
            {
                throw new ArgumentException($"The {gameType} ability {definition.Id} has no handler.", nameof(handlers));
            }

            if (!abilities.TryAdd(definition.Id, (definition, handler)))
            {
                throw new ArgumentException($"The {gameType} catalog defines {definition.Id} twice.", nameof(definitions));
            }

            CheckParameters(gameType, definition, handler);
        }

        if (handlersById.Keys.FirstOrDefault(id => !abilities.ContainsKey(id)) is { } orphan)
        {
            throw new ArgumentException($"The {gameType} catalog has a handler for {orphan}, which it doesn't define.", nameof(handlers));
        }

        return new AbilityCatalog(gameType, version, definitionList, abilities, timing);
    }

    /// <summary>
    /// Whether <paramref name="definition"/> configures exactly the parameters <paramref name="handler"/> declares, each
    /// within range; how the engine checks a locked ability against the handler running it now (DEC-036).
    /// </summary>
    internal static bool Fits(AbilityDefinition definition, IAbilityHandler handler) =>
        ParameterProblem(definition, handler) is null;

    private static void CheckParameters(string gameType, AbilityDefinition definition, IAbilityHandler handler)
    {
        if (ParameterProblem(definition, handler) is { } problem)
        {
            throw new ArgumentException($"The {gameType} ability {definition.Id} {problem}.");
        }
    }

    private static string? ParameterProblem(AbilityDefinition definition, IAbilityHandler handler)
    {
        var expected = handler.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var (name, value) in definition.Parameters)
        {
            if (!expected.TryGetValue(name, out var parameter))
            {
                return $"configures {name}, which its handler doesn't use";
            }

            if (value < parameter.Min || value > parameter.Max)
            {
                return $"sets {name} to {value}, outside {parameter.Min} to {parameter.Max}";
            }
        }

        return expected.Keys.FirstOrDefault(name => !definition.Parameters.ContainsKey(name)) is { } missing
            ? $"doesn't configure {missing}"
            : null;
    }
}
