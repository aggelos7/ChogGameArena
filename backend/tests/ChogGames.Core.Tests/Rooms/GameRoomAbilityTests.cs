using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Core.Tests.Abilities;

namespace ChogGames.Core.Tests.Rooms;

/// <summary>
/// Choosing an ability in the lobby and locking it at start (STORY-033, NFT-013, NFT-015, DEC-035), and counting its
/// uses in the match (STORY-034, DEC-036).
/// </summary>
public class GameRoomAbilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static Player Guest()
    {
        Assert.True(PlayerDisplayName.TryCreate("Alice", out var name));
        return Player.CreateGuest(name, Now);
    }

    private static (GameRoom Room, Player Host, Player Bob) Pair()
    {
        var host = Guest();
        var bob = Guest();
        Assert.True(RoomCode.TryParse("K7QF3M", out var code));
        var room = GameRoom.Create(GameDefinition.Create(FakeAbilityGame.GameType, 2, 6), host, code, Now);
        room.Join(bob, Now);
        return (room, host, bob);
    }

    private static Dictionary<Guid, CharacterSnapshot> Characters(params Player[] players) =>
        players.ToDictionary(p => p.Id, _ => CharacterSnapshot.Standard(DefaultAvatar.Classic, Now));

    private static AbilitySnapshot Locked(string abilityId)
    {
        var catalog = FakeAbilityGame.Catalog();
        Assert.True(catalog.TryFind(abilityId, out var ability, out _));
        return AbilitySnapshot.Lock(catalog, ability);
    }

    private static RoomMember Member(GameRoom room, Player player) => room.Members.Single(m => m.PlayerId == player.Id);

    [Fact]
    public void A_choice_is_stored_and_bumps_the_version_once()
    {
        var (room, _, bob) = Pair();
        var version = room.Version;

        room.SelectAbility(bob.Id, "boost");
        room.SelectAbility(bob.Id, "boost");

        Assert.Equal(("boost", version + 1), (Member(room, bob).AbilityId, room.Version));
    }

    [Fact]
    public void Choosing_another_ability_makes_a_ready_member_not_ready()
    {
        var (room, _, bob) = Pair();
        room.SelectAbility(bob.Id, "boost");
        room.MarkReady(bob);

        room.SelectAbility(bob.Id, "boost");
        Assert.True(Member(room, bob).IsReadyWith(bob));

        room.SelectAbility(bob.Id, "steal");
        Assert.False(Member(room, bob).IsReadyWith(bob));
    }

    [Fact]
    public void Withdrawing_ready_keeps_the_choice()
    {
        var (room, _, bob) = Pair();
        room.SelectAbility(bob.Id, "boost");
        room.MarkReady(bob);

        room.MarkNotReady(bob.Id);

        Assert.Equal("boost", Member(room, bob).AbilityId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Boost")]
    [InlineData("boost!")]
    public void Malformed_ids_are_refused(string abilityId)
    {
        var (room, _, bob) = Pair();

        Assert.Throws<ArgumentException>(() => room.SelectAbility(bob.Id, abilityId));
        Assert.Null(Member(room, bob).AbilityId);
    }

    [Fact]
    public void Only_members_of_a_waiting_room_choose()
    {
        var (room, host, bob) = Pair();

        Assert.Throws<InvalidOperationException>(() => room.SelectAbility(Guid.NewGuid(), "boost"));

        room.SelectAbility(host.Id, "boost");
        room.SelectAbility(bob.Id, "steal");
        room.MarkReady(host);
        room.MarkReady(bob);
        room.Start(host.Id, [host, bob], Characters(host, bob), TimeSpan.FromSeconds(20), ScriptedRandomSource.KeepOrder, Now,
            new Dictionary<Guid, AbilitySnapshot> { [host.Id] = Locked("boost"), [bob.Id] = Locked("steal") });
        var version = room.Version;

        Assert.Throws<InvalidOperationException>(() => room.SelectAbility(bob.Id, "boost"));
        Assert.Throws<InvalidOperationException>(() => room.DropAbility(bob.Id));
        Assert.Equal(("steal", "steal", version), (Member(room, bob).AbilityId, Member(room, bob).Ability!.Id, room.Version));
    }

    [Fact]
    public void Dropping_a_choice_clears_it_and_makes_the_member_not_ready()
    {
        var (room, _, bob) = Pair();
        room.SelectAbility(bob.Id, "boost");
        room.MarkReady(bob);
        var version = room.Version;

        room.DropAbility(bob.Id);
        room.DropAbility(bob.Id);

        Assert.Equal((null, false, version + 1), (Member(room, bob).AbilityId, Member(room, bob).IsReadyWith(bob), room.Version));
    }

    [Fact]
    public void The_start_locks_each_members_snapshot()
    {
        var (room, host, bob) = Pair();
        room.SelectAbility(host.Id, "boost");
        room.SelectAbility(bob.Id, "steal");
        room.MarkReady(host);
        room.MarkReady(bob);

        room.Start(host.Id, [host, bob], Characters(host, bob), TimeSpan.FromSeconds(20), ScriptedRandomSource.KeepOrder, Now,
            new Dictionary<Guid, AbilitySnapshot> { [host.Id] = Locked("boost"), [bob.Id] = Locked("steal") });

        Assert.Equal(Locked("boost"), Member(room, host).Ability);
        Assert.Equal(Locked("steal"), Member(room, bob).Ability);
    }

    [Fact]
    public void Only_the_chosen_ability_can_be_locked()
    {
        var (room, host, bob) = Pair();
        room.SelectAbility(host.Id, "boost");
        room.SelectAbility(bob.Id, "boost");
        room.MarkReady(host);
        room.MarkReady(bob);

        Assert.Throws<InvalidOperationException>(() => room.Start(
            host.Id, [host, bob], Characters(host, bob), TimeSpan.FromSeconds(20), ScriptedRandomSource.KeepOrder, Now,
            new Dictionary<Guid, AbilitySnapshot> { [host.Id] = Locked("boost"), [bob.Id] = Locked("steal") }));
        Assert.Equal(RoomStatus.Waiting, room.Status);
        Assert.All(room.Members, m => Assert.Null(m.Ability));
    }

    [Fact]
    public void A_game_without_abilities_starts_without_any()
    {
        var (room, host, bob) = Pair();
        room.MarkReady(host);
        room.MarkReady(bob);

        room.Start(host.Id, [host, bob], Characters(host, bob), TimeSpan.FromSeconds(20), ScriptedRandomSource.KeepOrder, Now);

        Assert.All(room.Members, m => Assert.Null(m.Ability));
    }

    [Fact]
    public void A_snapshot_copies_the_definition_with_the_catalog_version()
    {
        var catalog = AbilityCatalog.Create(
            FakeAbilityGame.GameType, 4, [FakeAbilityGame.Boost(40), FakeAbilityGame.Steal()],
            [new FakeAbilityGame.BoostHandler(), new FakeAbilityGame.StealHandler()], new FakeAbilityGame.StateWindow());
        Assert.True(catalog.TryFind("boost", out var boost, out _));

        var snapshot = AbilitySnapshot.Lock(catalog, boost);

        Assert.Equal(("boost", 4, "Boost", AbilityTargetKind.None, 1), (snapshot.Id, snapshot.CatalogVersion, snapshot.Name, snapshot.Target, snapshot.MaxUses));
        Assert.Equal(["before_reward"], snapshot.Windows);
        Assert.Equal(40m, snapshot.Parameters["percent"]);
        Assert.Equal(snapshot, AbilitySnapshot.Restore("boost", 4, "Boost", boost.Description, AbilityTargetKind.None, 1, ["before_reward"],
            new Dictionary<string, decimal> { ["percent"] = 40 }));
    }

    [Fact]
    public void A_snapshot_is_only_taken_of_the_catalogs_own_definition()
    {
        Assert.Throws<ArgumentException>(() => AbilitySnapshot.Lock(FakeAbilityGame.Catalog(), FakeAbilityGame.Boost(percent: 99)));
    }

    /// <summary>A started pair, seated host first, both locking <c>boost</c>; the host plays the first turn.</summary>
    private static (GameRoom Room, Player Host, Player Bob) StartedPair()
    {
        var (room, host, bob) = Pair();
        room.SelectAbility(host.Id, "boost");
        room.SelectAbility(bob.Id, "boost");
        room.MarkReady(host);
        room.MarkReady(bob);
        room.Start(host.Id, [host, bob], Characters(host, bob), TimeSpan.FromSeconds(20), ScriptedRandomSource.KeepOrder, Now,
            new Dictionary<Guid, AbilitySnapshot> { [host.Id] = Locked("boost"), [bob.Id] = Locked("boost") });
        return (room, host, bob);
    }

    private static MatchCommand UseCommand(GameRoom room) =>
        new(Guid.NewGuid(), room.Id, room.Version, MatchCommand.UseAbility, JsonDocument.Parse("""{"abilityId":"boost"}""").RootElement.Clone());

    [Fact]
    public void A_use_is_counted_and_keeps_the_turn()
    {
        var (room, host, _) = StartedPair();
        var (version, turn, endsAt) = (room.Version, room.TurnNumber, room.TurnEndsAt);
        Assert.Equal((0, 1), (Member(room, host).AbilityUses, Member(room, host).AbilityUsesLeft));

        var action = room.ApplyAbility(host.Id, UseCommand(room), "boost", """{"reward":125}""", Now.AddSeconds(5));

        Assert.Equal((1, 0), (Member(room, host).AbilityUses, Member(room, host).AbilityUsesLeft));
        Assert.Equal((version + 1, version + 1, MatchCommand.UseAbility), (room.Version, action.Version, action.Type));
        Assert.Equal((host.Id, turn, endsAt, """{"reward":125}"""), (room.CurrentPlayerId, room.TurnNumber, room.TurnEndsAt, room.State));
    }

    [Fact]
    public void A_use_resets_missed_turns()
    {
        var (room, host, bob) = StartedPair();
        room.TimeOutTurn(null, 3, Now.AddSeconds(20));
        room.TimeOutTurn(null, 3, Now.AddSeconds(40));
        Assert.Equal(1, Member(room, host).MissedTurns);

        room.ApplyAbility(host.Id, UseCommand(room), "boost", "{}", Now.AddSeconds(41));

        Assert.Equal((0, 1), (Member(room, host).MissedTurns, Member(room, bob).MissedTurns));
    }

    [Fact]
    public void A_use_past_the_limit_or_of_another_ability_is_refused_unchanged()
    {
        var (room, host, _) = StartedPair();
        room.ApplyAbility(host.Id, UseCommand(room), "boost", """{"n":1}""", Now);
        var version = room.Version;

        Assert.Throws<InvalidOperationException>(() => room.ApplyAbility(host.Id, UseCommand(room), "boost", """{"n":2}""", Now));
        Assert.Throws<InvalidOperationException>(() => room.ApplyAbility(host.Id, UseCommand(room), "steal", """{"n":2}""", Now));
        Assert.Equal((version, 1, """{"n":1}"""), (room.Version, Member(room, host).AbilityUses, room.State));
    }

    [Fact]
    public void Only_the_current_player_uses_an_ability()
    {
        var (room, _, bob) = StartedPair();

        Assert.Throws<InvalidOperationException>(() => room.ApplyAbility(bob.Id, UseCommand(room), "boost", "{}", Now));
        Assert.Equal(0, Member(room, bob).AbilityUses);
    }

    [Fact]
    public void A_member_without_a_locked_ability_has_no_uses()
    {
        var (room, host, bob) = Pair();
        room.MarkReady(host);
        room.MarkReady(bob);
        room.Start(host.Id, [host, bob], Characters(host, bob), TimeSpan.FromSeconds(20), ScriptedRandomSource.KeepOrder, Now);

        Assert.Null(Member(room, host).AbilityUsesLeft);
        Assert.Throws<InvalidOperationException>(() => room.ApplyAbility(host.Id, UseCommand(room), "boost", "{}", Now));
    }
}
