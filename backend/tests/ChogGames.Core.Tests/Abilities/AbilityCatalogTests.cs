using ChogGames.Core.Abilities;
using static ChogGames.Core.Tests.Abilities.FakeAbilityGame;

namespace ChogGames.Core.Tests.Abilities;

public class AbilityCatalogTests
{
    [Fact]
    public void Definition_keeps_its_configuration_and_defaults_to_one_use()
    {
        var boost = Boost();

        Assert.Equal(("boost", "Boost", AbilityTargetKind.None, 1), (boost.Id, boost.Name, boost.Target, boost.MaxUses));
        Assert.Equal(["before_reward"], boost.Windows);
        Assert.Equal(25m, boost.Parameters["percent"]);
        Assert.Equal(Boost(), boost);
        Assert.NotEqual(Boost(percent: 30), boost);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Boost")]
    [InlineData("reward-boost")]
    [InlineData("1boost")]
    [InlineData("a_very_long_ability_identifier_xx")]
    public void Definition_refuses_a_malformed_id(string id)
    {
        Assert.Throws<ArgumentException>(() =>
            AbilityDefinition.Create(id, "Boost", "Raise it.", AbilityTargetKind.None, ["before_reward"]));
    }

    [Theory]
    [InlineData("", "Raise it.", 1)]
    [InlineData("Boost", " ", 1)]
    [InlineData("Boost", "Raise it.", 0)]
    [InlineData("Boost", "Raise it.", 11)]
    public void Definition_refuses_a_missing_name_or_description_and_out_of_range_uses(string name, string description, int maxUses)
    {
        Assert.Throws<ArgumentException>(() =>
            AbilityDefinition.Create("boost", name, description, AbilityTargetKind.None, ["before_reward"], maxUses: maxUses));
    }

    [Fact]
    public void Definition_refuses_missing_duplicate_or_malformed_windows_and_parameters()
    {
        Assert.Throws<ArgumentException>(() => AbilityDefinition.Create("boost", "Boost", "Raise it.", AbilityTargetKind.None, []));
        Assert.Throws<ArgumentException>(() =>
            AbilityDefinition.Create("boost", "Boost", "Raise it.", AbilityTargetKind.None, ["own_turn", "own_turn"]));
        Assert.Throws<ArgumentException>(() =>
            AbilityDefinition.Create("boost", "Boost", "Raise it.", AbilityTargetKind.None, ["Own Turn"]));
        Assert.Throws<ArgumentException>(() =>
            AbilityDefinition.Create("boost", "Boost", "Raise it.", AbilityTargetKind.None, ["own_turn"], new Dictionary<string, decimal> { ["Percent"] = 1 }));
        Assert.Throws<ArgumentException>(() =>
            AbilityDefinition.Create("boost", "Boost", "Raise it.", (AbilityTargetKind)7, ["own_turn"]));
    }

    [Fact]
    public void Catalog_offers_its_definitions_in_order_with_their_handlers()
    {
        var boost = new BoostHandler();

        var catalog = Catalog(boost);

        Assert.Equal((GameType, 1), (catalog.GameType, catalog.Version));
        Assert.Equal(["boost", "steal"], catalog.Definitions.Select(d => d.Id));
        Assert.True(catalog.TryFind("boost", out var definition, out var handler));
        Assert.Equal(Boost(), definition);
        Assert.Same(boost, handler);
        Assert.False(catalog.TryFind("Boost", out _, out _));
        Assert.False(catalog.Contains("reward_boost"));
    }

    [Fact]
    public void Catalog_refuses_a_bad_game_or_version_or_no_abilities()
    {
        Assert.Throws<ArgumentException>(() => AbilityCatalog.Create("", 1, [Boost()], [new BoostHandler()], new StateWindow()));
        Assert.Throws<ArgumentOutOfRangeException>(() => AbilityCatalog.Create(GameType, 0, [Boost()], [new BoostHandler()], new StateWindow()));
        Assert.Throws<ArgumentException>(() => AbilityCatalog.Create(GameType, 1, [], [], new StateWindow()));
        Assert.Throws<ArgumentNullException>(() => AbilityCatalog.Create(GameType, 1, [Boost()], [new BoostHandler()], null!));
    }

    [Fact]
    public void Catalog_refuses_a_definition_without_a_handler_a_handler_without_a_definition_and_duplicates()
    {
        Assert.Contains("no handler", Refusal([Boost(), Steal()], [new BoostHandler()]));
        Assert.Contains("doesn't define", Refusal([Boost()], [new BoostHandler(), new StealHandler()]));
        Assert.Contains("twice", Refusal([Boost(), Boost()], [new BoostHandler()]));
        Assert.Contains("two handlers", Refusal([Boost()], [new BoostHandler(), new BoostHandler()]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-25)]
    public void Catalog_refuses_a_parameter_outside_its_handlers_range(decimal percent)
    {
        Assert.Contains("outside 1 to 100", Refusal([Boost(percent)], [new BoostHandler()]));
    }

    [Fact]
    public void Catalog_refuses_missing_or_unexpected_parameters()
    {
        var bare = AbilityDefinition.Create("boost", "Boost", "Raise it.", AbilityTargetKind.None, ["before_reward"]);
        var extra = AbilityDefinition.Create(
            "boost", "Boost", "Raise it.", AbilityTargetKind.None, ["before_reward"],
            new Dictionary<string, decimal> { ["percent"] = 25, ["bonus"] = 5 });

        Assert.Contains("doesn't configure percent", Refusal([bare], [new BoostHandler()]));
        Assert.Contains("configures bonus", Refusal([extra], [new BoostHandler()]));
    }

    [Fact]
    public void Engine_refuses_two_catalogs_for_one_game_and_has_none_for_other_games()
    {
        Assert.Throws<ArgumentException>(() => new AbilityEngine([Catalog(), Catalog()]));

        var engine = new AbilityEngine([Catalog()]);

        Assert.Equal(GameType, engine.CatalogFor(GameType)?.GameType);
        Assert.Null(engine.CatalogFor("chog-life"));
    }

    private static string Refusal(AbilityDefinition[] definitions, IAbilityHandler[] handlers) =>
        Assert.Throws<ArgumentException>(() => AbilityCatalog.Create(GameType, 1, definitions, handlers, new StateWindow())).Message;
}
