using System.Text.Json;
using System.Text.Json.Nodes;
using ChogGames.Core.Abilities;

namespace ChogGames.Core.Tests.Abilities;

/// <summary>
/// A tiny game with two abilities for the engine tests. Its state is <c>{"window": "...", "reward": 100, "cards": {"seat": n}}</c>:
/// <c>boost</c> raises the pending reward by its configured percent; <c>steal</c> takes a server-random card from an
/// opponent. The handlers count their calls, so tests can show refusals never reach them.
/// </summary>
public static class FakeAbilityGame
{
    public const string GameType = "chog-test";

    public static AbilityDefinition Boost(decimal percent = 25) => AbilityDefinition.Create(
        "boost", "Boost", "Raise the pending reward.", AbilityTargetKind.None, ["before_reward"],
        new Dictionary<string, decimal> { ["percent"] = percent });

    public static AbilityDefinition Steal() => AbilityDefinition.Create(
        "steal", "Steal", "Take a random card from an opponent.", AbilityTargetKind.Opponent, ["own_turn", "before_reward"]);

    public static AbilityCatalog Catalog(BoostHandler? boost = null, StealHandler? steal = null) =>
        AbilityCatalog.Create(GameType, 1, [Boost(), Steal()], [boost ?? new BoostHandler(), steal ?? new StealHandler()], new StateWindow());

    public static JsonElement State(string window, int reward = 100, int targetCards = 3) =>
        JsonSerializer.SerializeToElement(new { window, reward, cards = new Dictionary<string, int> { ["2"] = targetCards } });

    /// <summary>The window is whatever the state says; there is none before the first state.</summary>
    public sealed class StateWindow : IAbilityTiming
    {
        public string? CurrentWindow(AbilityContext context) =>
            context.State is { } state && state.TryGetProperty("window", out var window) ? window.GetString() : null;
    }

    public sealed class BoostHandler : IAbilityHandler
    {
        public int Checks { get; private set; }

        public int Executions { get; private set; }

        public string AbilityId => "boost";

        public IReadOnlyList<AbilityParameter> Parameters { get; } = [new("percent", 1, 100)];

        public string? CanExecute(AbilityContext context)
        {
            Checks++;
            return context.State!.Value.GetProperty("reward").GetInt32() > 0 ? null : "no_reward_pending";
        }

        public JsonElement Execute(AbilityContext context)
        {
            Executions++;
            var state = JsonNode.Parse(context.State!.Value.GetRawText())!;
            var reward = state["reward"]!.GetValue<int>();
            state["reward"] = (int)(reward * (100 + context.Ability.Parameters["percent"]) / 100);
            return JsonSerializer.SerializeToElement(state);
        }
    }

    public sealed class StealHandler : IAbilityHandler
    {
        public int Checks { get; private set; }

        public string AbilityId => "steal";

        public IReadOnlyList<AbilityParameter> Parameters { get; } = [];

        public string? CanExecute(AbilityContext context)
        {
            Checks++;
            return CardsOf(context) > 0 ? null : "target_has_no_cards";
        }

        /// <summary>Draws which of the target's cards is taken and records it as <c>stolen</c>.</summary>
        public JsonElement Execute(AbilityContext context)
        {
            var state = JsonNode.Parse(context.State!.Value.GetRawText())!;
            var seat = context.Target!.Seat.ToString();
            state["stolen"] = context.Random.Next(0, CardsOf(context));
            state["cards"]![seat] = CardsOf(context) - 1;
            return JsonSerializer.SerializeToElement(state);
        }

        private static int CardsOf(AbilityContext context) =>
            context.State!.Value.GetProperty("cards").TryGetProperty(context.Target!.Seat.ToString(), out var cards) ? cards.GetInt32() : 0;
    }
}
