using System.Collections.Concurrent;
using System.Security.Cryptography;
using ChogGames.Core.Games;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Characters;
using ChogGames.Infrastructure.Matches;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Tests.Rooms;

public class LobbyServiceTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>
{
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeNftProvider chain = new();

    private LobbyService Service(ChogGamesDbContext db) => new(
        db,
        new CharacterEligibilityService(chain, clock, NullLogger<CharacterEligibilityService>.Instance),
        Options.Create(new MatchOptions()),
        ScriptedRandomSource.KeepOrder,
        clock,
        NullLogger<LobbyService>.Instance);

    private async Task<Player> SavedGuest(string chog = "classic")
    {
        Assert.True(PlayerDisplayName.TryCreate("Alice", out var name));
        Assert.True(DefaultAvatar.TryFind(chog, out var standard));
        var player = Player.CreateGuest(name, clock.GetUtcNow());
        player.SelectStandardCharacter(standard);
        await using var db = database.CreateDbContext();
        db.Players.Add(player);
        await db.SaveChangesAsync();
        return player;
    }

    /// <summary>A wallet player holding token <paramref name="tokenId"/> and having selected it.</summary>
    private async Task<(Player Player, WalletAddress Wallet)> SavedHolder(ulong tokenId)
    {
        Assert.True(PlayerDisplayName.TryCreate("Holder", out var name));
        Assert.True(WalletAddress.TryParse("0x" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant(), out var wallet));
        var nft = new OwnedNft(NftTokenId.From(tokenId), $"Chog #{tokenId}", null, [new NftTrait("Eyes", "Laser")]);
        chain.Holdings[wallet] = [nft];
        var player = Player.CreateForWallet(wallet, name, clock.GetUtcNow());
        player.SelectNftCharacter(nft.TokenId);
        await using var db = database.CreateDbContext();
        db.Players.Add(player);
        await db.SaveChangesAsync();
        return (player, wallet);
    }

    private async Task<GameRoom> SavedRoom(Player host, params Player[] others)
    {
        await using var db = database.CreateDbContext();
        var game = await db.GameDefinitions.AsNoTracking().SingleAsync(g => g.Id == GameDefinition.ChogLife);
        var room = GameRoom.Create(game, host, new SecureRoomCodeGenerator().Next(), clock.GetUtcNow());
        foreach (var other in others)
        {
            room.Join(other, clock.GetUtcNow());
        }

        db.GameRooms.Add(room);
        await db.SaveChangesAsync();
        return room;
    }

    private async Task<RoomResult> Ready(Player player, GameRoom room, bool ready = true)
    {
        await using var db = database.CreateDbContext();
        return await Service(db).SetReadyAsync(player, Code(room), ready, CancellationToken.None);
    }

    private async Task<RoomResult> Start(Player caller, GameRoom room)
    {
        await using var db = database.CreateDbContext();
        return await Service(db).StartAsync(caller, Code(room), CancellationToken.None);
    }

    private static RoomCode Code(GameRoom room)
    {
        Assert.True(RoomCode.TryParse(room.Code, out var code));
        return code;
    }

    private async Task<RoomResult> Leave(Player player, GameRoom room)
    {
        await using var db = database.CreateDbContext();
        return await Service(db).LeaveAsync(player, Code(room), CancellationToken.None);
    }

    private RoomService Rooms(ChogGamesDbContext db) =>
        new(db, new OpenCatalog(), new SecureRoomCodeGenerator(), clock, NullLogger<RoomService>.Instance);

    private async Task<RoomResult> JoinRoom(Player player, GameRoom room)
    {
        await using var db = database.CreateDbContext();
        return await Rooms(db).JoinAsync(player, Code(room), CancellationToken.None);
    }

    private async Task<GameRoom> Stored(GameRoom room)
    {
        await using var db = database.CreateDbContext();
        return await db.GameRooms.AsNoTracking().SingleAsync(r => r.Id == room.Id);
    }

    private async Task<Player> StoredPlayer(Player player)
    {
        await using var db = database.CreateDbContext();
        return await db.Players.AsNoTracking().SingleAsync(p => p.Id == player.Id);
    }

    private async Task<bool> IsReady(GameRoom room, Player player)
    {
        var stored = await Stored(room);
        return stored.Members.Single(m => m.PlayerId == player.Id).IsReadyWith(await StoredPlayer(player));
    }

    private async Task SelectStandard(Player player, string chog)
    {
        Assert.True(DefaultAvatar.TryFind(chog, out var standard));
        await using var db = database.CreateDbContext();
        var stored = await db.Players.SingleAsync(p => p.Id == player.Id);
        stored.SelectStandardCharacter(standard);
        await db.SaveChangesAsync();
    }

    private async Task<(GameRoom Room, Player Host, Player Bob)> ReadyPair()
    {
        var host = await SavedGuest();
        var bob = await SavedGuest("mint");
        var room = await SavedRoom(host, bob);
        Assert.Equal(RoomOutcome.ReadyChanged, (await Ready(host, room)).Outcome);
        Assert.Equal(RoomOutcome.ReadyChanged, (await Ready(bob, room)).Outcome);
        return (room, host, bob);
    }

    [PostgresFact]
    public async Task Members_mark_ready_with_their_selected_chog_and_back()
    {
        var host = await SavedGuest();
        var bob = await SavedGuest("mint");
        var room = await SavedRoom(host, bob);

        var result = await Ready(bob, room);

        Assert.Equal(RoomOutcome.ReadyChanged, result.Outcome);
        var member = (await Stored(room)).Members.Single(m => m.PlayerId == bob.Id);
        Assert.Equal((CharacterKind.Standard, "mint"), (member.ReadyCharacterKind, member.ReadyCharacterId));
        Assert.False(await IsReady(room, host));

        Assert.Equal(RoomOutcome.ReadyChanged, (await Ready(bob, room)).Outcome);
        Assert.True(await IsReady(room, bob));

        Assert.Equal(RoomOutcome.ReadyChanged, (await Ready(bob, room, ready: false)).Outcome);
        Assert.False(await IsReady(room, bob));
    }

    [PostgresFact]
    public async Task Ready_records_the_stored_chog_not_the_callers_copy()
    {
        var host = await SavedGuest();
        var room = await SavedRoom(host);
        await SelectStandard(host, "gold");

        await Ready(host, room);

        var member = Assert.Single((await Stored(room)).Members);
        Assert.Equal("gold", member.ReadyCharacterId);
    }

    [PostgresFact]
    public async Task Choosing_another_chog_cancels_ready()
    {
        var (room, _, bob) = await ReadyPair();

        await SelectStandard(bob, "berry");

        Assert.False(await IsReady(room, bob));
    }

    [PostgresFact]
    public async Task Non_members_and_unknown_codes_are_not_found()
    {
        var host = await SavedGuest();
        var room = await SavedRoom(host);
        var stranger = await SavedGuest();

        Assert.Equal(RoomOutcome.RoomNotFound, (await Ready(stranger, room)).Outcome);
        Assert.Equal(RoomOutcome.RoomNotFound, (await Start(stranger, room)).Outcome);

        Assert.True(RoomCode.TryParse("ZZZZZZ", out var unknown));
        await using var db = database.CreateDbContext();
        Assert.Equal(RoomOutcome.RoomNotFound, (await Service(db).SetReadyAsync(host, unknown, true, CancellationToken.None)).Outcome);
        Assert.Equal(RoomOutcome.RoomNotFound, (await Service(db).StartAsync(host, unknown, CancellationToken.None)).Outcome);
        Assert.All((await Stored(room)).Members, m => Assert.Null(m.ReadyCharacterKind));
    }

    [PostgresFact]
    public async Task Host_starts_when_everyone_is_ready()
    {
        var (room, host, _) = await ReadyPair();
        clock.Advance(TimeSpan.FromMinutes(3));

        var result = await Start(host, room);

        Assert.Equal(RoomOutcome.Started, result.Outcome);
        Assert.Equal(RoomStatus.Active, result.Room!.Status);
        var stored = await Stored(room);
        Assert.Equal((RoomStatus.Active, clock.GetUtcNow()), (stored.Status, stored.StartedAt));
    }

    [PostgresFact]
    public async Task Only_the_host_can_start()
    {
        var (room, _, bob) = await ReadyPair();

        Assert.Equal(RoomOutcome.NotHost, (await Start(bob, room)).Outcome);
        Assert.Equal(RoomStatus.Waiting, (await Stored(room)).Status);
    }

    [PostgresFact]
    public async Task Host_alone_cannot_start()
    {
        var host = await SavedGuest();
        var room = await SavedRoom(host);
        await Ready(host, room);

        Assert.Equal(RoomOutcome.NotEnoughPlayers, (await Start(host, room)).Outcome);
        Assert.Equal(RoomStatus.Waiting, (await Stored(room)).Status);
    }

    [PostgresFact]
    public async Task Everyone_including_the_host_must_be_ready()
    {
        var host = await SavedGuest();
        var bob = await SavedGuest();
        var room = await SavedRoom(host, bob);
        await Ready(bob, room);

        Assert.Equal(RoomOutcome.PlayersNotReady, (await Start(host, room)).Outcome);

        await Ready(host, room);
        await Ready(bob, room, ready: false);
        Assert.Equal(RoomOutcome.PlayersNotReady, (await Start(host, room)).Outcome);
        Assert.Equal(RoomStatus.Waiting, (await Stored(room)).Status);
    }

    [PostgresFact]
    public async Task Changing_chog_after_ready_blocks_the_start()
    {
        var (room, host, bob) = await ReadyPair();
        await SelectStandard(bob, "berry");

        Assert.Equal(RoomOutcome.PlayersNotReady, (await Start(host, room)).Outcome);
        Assert.Equal(RoomStatus.Waiting, (await Stored(room)).Status);
    }

    [PostgresFact]
    public async Task Six_ready_players_start()
    {
        var host = await SavedGuest();
        var others = new List<Player>();
        for (var i = 0; i < 5; i++)
        {
            others.Add(await SavedGuest());
        }

        var room = await SavedRoom(host, [.. others]);
        foreach (var player in others.Prepend(host))
        {
            await Ready(player, room);
        }

        Assert.Equal(RoomOutcome.Started, (await Start(host, room)).Outcome);
    }

    [PostgresFact]
    public async Task Owned_nfts_are_checked_live_at_start()
    {
        var host = await SavedGuest();
        var (holder, wallet) = await SavedHolder(7);
        var room = await SavedRoom(host, holder);
        await Ready(host, room);
        await Ready(holder, room);

        Assert.Equal(RoomOutcome.Started, (await Start(host, room)).Outcome);
        Assert.Equal((wallet, "7"), Assert.Single(chain.LiveChecks));
    }

    [PostgresFact]
    public async Task A_transferred_nft_blocks_the_start_and_unreadies_only_its_player()
    {
        var host = await SavedGuest();
        var (holder, wallet) = await SavedHolder(7);
        var room = await SavedRoom(host, holder);
        await Ready(host, room);
        await Ready(holder, room);
        chain.Holdings[wallet] = [];

        var result = await Start(host, room);

        Assert.Equal(RoomOutcome.CharacterUnavailable, result.Outcome);
        Assert.Equal(RoomStatus.Waiting, (await Stored(room)).Status);
        Assert.False(await IsReady(room, holder));
        Assert.True(await IsReady(room, host));
    }

    [PostgresFact]
    public async Task Start_waits_when_ownership_cannot_be_read()
    {
        var host = await SavedGuest();
        var (holder, _) = await SavedHolder(7);
        var room = await SavedRoom(host, holder);
        await Ready(host, room);
        await Ready(holder, room);
        chain.Down = true;

        Assert.Equal(RoomOutcome.OwnershipUnavailable, (await Start(host, room)).Outcome);
        Assert.Equal(RoomStatus.Waiting, (await Stored(room)).Status);
        Assert.True(await IsReady(room, holder));

        chain.Down = false;
        Assert.Equal(RoomOutcome.Started, (await Start(host, room)).Outcome);
    }

    [PostgresTheory]
    [InlineData(RoomStatus.Active, RoomOutcome.RoomStarted)]
    [InlineData(RoomStatus.Completed, RoomOutcome.RoomClosed)]
    [InlineData(RoomStatus.Cancelled, RoomOutcome.RoomClosed)]
    [InlineData(RoomStatus.Abandoned, RoomOutcome.RoomClosed)]
    public async Task Nothing_changes_once_the_room_is_not_waiting(RoomStatus status, RoomOutcome expected)
    {
        var (room, host, bob) = await ReadyPair();
        await SetStatus(room, status);

        Assert.Equal(expected, (await Ready(bob, room, ready: false)).Outcome);
        Assert.Equal(expected, (await Start(host, room)).Outcome);
        Assert.True(await IsReady(room, bob));
    }

    [PostgresFact]
    public async Task Starting_twice_is_refused_as_started()
    {
        var (room, host, _) = await ReadyPair();
        Assert.Equal(RoomOutcome.Started, (await Start(host, room)).Outcome);

        Assert.Equal(RoomOutcome.RoomStarted, (await Start(host, room)).Outcome);
    }

    [PostgresFact]
    public async Task A_start_racing_a_join_never_starts_with_an_unready_member()
    {
        for (var round = 0; round < 10; round++)
        {
            var (room, host, _) = await ReadyPair();
            var carol = await SavedGuest();

            var start = Task.Run(() => Start(host, room));
            var join = Task.Run(() => JoinRoom(carol, room));
            var (started, joined) = (await start, await join);

            var stored = await Stored(room);
            if (started.Outcome == RoomOutcome.Started)
            {
                Assert.Equal(RoomOutcome.RoomStarted, joined.Outcome);
                Assert.Equal(2, stored.Members.Count);
            }
            else
            {
                Assert.Equal(RoomOutcome.PlayersNotReady, started.Outcome);
                Assert.Equal(RoomOutcome.Joined, joined.Outcome);
                Assert.Equal((RoomStatus.Waiting, 3), (stored.Status, stored.Members.Count));
            }
        }
    }

    [PostgresFact]
    public async Task Concurrent_starts_start_the_room_once()
    {
        var (room, host, _) = await ReadyPair();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Start(host, room))));

        Assert.Single(results, r => r.Outcome == RoomOutcome.Started);
        Assert.All(results.Where(r => r.Outcome != RoomOutcome.Started), r => Assert.Equal(RoomOutcome.RoomStarted, r.Outcome));
    }

    [PostgresFact]
    public async Task A_member_who_leaves_is_removed_and_can_join_another_room()
    {
        var host = await SavedGuest();
        var bob = await SavedGuest();
        var carol = await SavedGuest();
        var room = await SavedRoom(host, bob, carol);
        await Ready(host, room);
        await Ready(carol, room);

        var result = await Leave(bob, room);

        Assert.Equal(RoomOutcome.Left, result.Outcome);
        var stored = await Stored(room);
        Assert.Equal((RoomStatus.Waiting, 2), (stored.Status, stored.Members.Count));
        Assert.False(stored.IsMember(bob.Id));
        Assert.True(await IsReady(room, host));
        Assert.True(await IsReady(room, carol));

        var other = await SavedRoom(await SavedGuest());
        Assert.Equal(RoomOutcome.Joined, (await JoinRoom(bob, other)).Outcome);
    }

    [PostgresFact]
    public async Task The_host_leaving_cancels_the_room_and_frees_everyone()
    {
        var (room, host, bob) = await ReadyPair();

        Assert.Equal(RoomOutcome.Left, (await Leave(host, room)).Outcome);

        var stored = await Stored(room);
        Assert.Equal((RoomStatus.Cancelled, 2), (stored.Status, stored.Members.Count));
        Assert.Equal(RoomOutcome.RoomClosed, (await JoinRoom(await SavedGuest(), room)).Outcome);

        var other = await SavedRoom(await SavedGuest());
        Assert.Equal(RoomOutcome.Joined, (await JoinRoom(bob, other)).Outcome);
        await using var db = database.CreateDbContext();
        var game = await db.GameDefinitions.AsNoTracking().SingleAsync(g => g.Id == GameDefinition.ChogLife);
        db.GameRooms.Add(GameRoom.Create(game, host, new SecureRoomCodeGenerator().Next(), clock.GetUtcNow()));
        await db.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task Leaving_is_refused_for_non_members_and_after_leaving()
    {
        var host = await SavedGuest();
        var bob = await SavedGuest();
        var room = await SavedRoom(host, bob);

        Assert.Equal(RoomOutcome.RoomNotFound, (await Leave(await SavedGuest(), room)).Outcome);
        Assert.Equal(RoomOutcome.Left, (await Leave(bob, room)).Outcome);
        Assert.Equal(RoomOutcome.RoomNotFound, (await Leave(bob, room)).Outcome);
        Assert.Equal(RoomOutcome.Left, (await Leave(host, room)).Outcome);
        Assert.Equal(RoomOutcome.RoomClosed, (await Leave(host, room)).Outcome);
    }

    [PostgresTheory]
    [InlineData(RoomStatus.Completed)]
    [InlineData(RoomStatus.Abandoned)]
    public async Task Nobody_leaves_a_match_that_has_ended(RoomStatus status)
    {
        var (room, host, bob) = await ReadyPair();
        Assert.Equal(RoomOutcome.Started, (await Start(host, room)).Outcome);
        await SetStatus(room, status);

        Assert.Equal(RoomOutcome.RoomClosed, (await Leave(bob, room)).Outcome);
        Assert.Equal(RoomOutcome.RoomClosed, (await Leave(host, room)).Outcome);
        var stored = await Stored(room);
        Assert.Equal((status, 2), (stored.Status, stored.Members.Count));
        Assert.All(stored.Members, m => Assert.False(m.HasLeft));
    }

    [PostgresFact]
    public async Task Starting_stores_each_members_character_snapshot()
    {
        var host = await SavedGuest("gold");
        var (holder, wallet) = await SavedHolder(7);
        var room = await SavedRoom(host, holder);
        await Ready(host, room);
        await Ready(holder, room);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(RoomOutcome.Started, (await Start(host, room)).Outcome);

        var stored = await Stored(room);
        var standard = stored.Members.Single(m => m.PlayerId == host.Id).Character!;
        Assert.Equal((CharacterKind.Standard, "gold", "Gold", (string?)null, clock.GetUtcNow()), (standard.Kind, standard.CharacterId, standard.Name, standard.WalletAddress, standard.VerifiedAt));
        var nft = stored.Members.Single(m => m.PlayerId == holder.Id).Character!;
        Assert.Equal((CharacterKind.Nft, "7", "Chog #7", wallet.Value), (nft.Kind, nft.CharacterId, nft.Name, nft.WalletAddress));
        Assert.Equal([new NftTrait("Eyes", "Laser")], nft.Traits);
    }

    [PostgresFact]
    public async Task A_refused_start_stores_no_snapshots()
    {
        var host = await SavedGuest();
        var (holder, wallet) = await SavedHolder(7);
        var room = await SavedRoom(host, holder);
        await Ready(host, room);
        await Ready(holder, room);
        chain.Holdings[wallet] = [];

        Assert.Equal(RoomOutcome.CharacterUnavailable, (await Start(host, room)).Outcome);

        Assert.All((await Stored(room)).Members, m => Assert.Null(m.Character));
    }

    [PostgresFact]
    public async Task The_snapshot_stays_when_the_nft_is_transferred_during_the_match()
    {
        var host = await SavedGuest();
        var (holder, wallet) = await SavedHolder(7);
        var room = await SavedRoom(host, holder);
        await Ready(host, room);
        await Ready(holder, room);
        Assert.Equal(RoomOutcome.Started, (await Start(host, room)).Outcome);

        chain.Holdings[wallet] = [];
        await SelectStandard(holder, "berry");

        var stored = await Stored(room);
        Assert.Equal((RoomStatus.Active, "7"), (stored.Status, stored.Members.Single(m => m.PlayerId == holder.Id).Character!.CharacterId));
    }

    [PostgresFact]
    public async Task Leaving_a_started_match_is_a_forfeit_and_frees_the_player()
    {
        var (room, host, bob, carol) = await StartedTrio();
        clock.Advance(TimeSpan.FromMinutes(4));

        Assert.Equal(RoomOutcome.Left, (await Leave(bob, room)).Outcome);

        var stored = await Stored(room);
        Assert.Equal((RoomStatus.Active, 3, (DateTimeOffset?)null), (stored.Status, stored.Members.Count, stored.EndedAt));
        var left = stored.Members.Single(m => m.PlayerId == bob.Id);
        Assert.Equal((clock.GetUtcNow(), true), (left.LeftAt, left.Character is not null));
        Assert.False(stored.HasLeft(carol.Id));

        Assert.Equal(RoomOutcome.RoomStarted, (await JoinRoom(bob, room)).Outcome);
        var other = await SavedRoom(await SavedGuest());
        Assert.Equal(RoomOutcome.Joined, (await JoinRoom(bob, other)).Outcome);
        Assert.Equal(RoomOutcome.AlreadyInRoom, (await JoinRoom(host, other)).Outcome);
    }

    [PostgresFact]
    public async Task A_match_started_before_snapshots_were_stored_can_be_left()
    {
        // Rooms that became Active before STORY-023 have members without a snapshot.
        var host = await SavedGuest();
        var bob = await SavedGuest();
        var carol = await SavedGuest();
        var room = await SavedRoom(host, bob, carol);
        await SetStatus(room, RoomStatus.Active);

        Assert.Equal(RoomOutcome.Left, (await Leave(bob, room)).Outcome);
        Assert.Equal(RoomOutcome.Left, (await Leave(host, room)).Outcome);

        var stored = await Stored(room);
        Assert.Equal(RoomStatus.Abandoned, stored.Status);
        Assert.All(stored.Members, m => Assert.Null(m.Character));
        Assert.True(stored.HasLeft(bob.Id) && stored.HasLeft(host.Id));
    }

    [PostgresFact]
    public async Task Leaving_a_match_again_changes_nothing()
    {
        var (room, _, bob, _) = await StartedTrio();
        Assert.Equal(RoomOutcome.Left, (await Leave(bob, room)).Outcome);
        var leftAt = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(RoomOutcome.AlreadyLeft, (await Leave(bob, room)).Outcome);

        var stored = await Stored(room);
        Assert.Equal((RoomStatus.Active, leftAt), (stored.Status, stored.Members.Single(m => m.PlayerId == bob.Id).LeftAt));
    }

    [PostgresFact]
    public async Task The_host_leaving_a_started_match_does_not_end_it()
    {
        var (room, host, bob, carol) = await StartedTrio();

        Assert.Equal(RoomOutcome.Left, (await Leave(host, room)).Outcome);

        var stored = await Stored(room);
        Assert.Equal(RoomStatus.Active, stored.Status);
        Assert.True(stored.IsPlaying(bob.Id) && stored.IsPlaying(carol.Id));
    }

    [PostgresFact]
    public async Task A_match_left_with_one_player_is_abandoned_and_frees_everyone()
    {
        var (room, host, bob) = await ReadyPair();
        Assert.Equal(RoomOutcome.Started, (await Start(host, room)).Outcome);
        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(RoomOutcome.Left, (await Leave(bob, room)).Outcome);

        var stored = await Stored(room);
        Assert.Equal((RoomStatus.Abandoned, clock.GetUtcNow()), (stored.Status, stored.EndedAt));
        Assert.Equal(RoomOutcome.RoomClosed, (await Leave(host, room)).Outcome);
        Assert.Equal(RoomOutcome.RoomClosed, (await JoinRoom(await SavedGuest(), room)).Outcome);
        var other = await SavedRoom(await SavedGuest());
        Assert.Equal(RoomOutcome.Joined, (await JoinRoom(host, other)).Outcome);
        Assert.Equal(RoomOutcome.Joined, (await JoinRoom(bob, other)).Outcome);
    }

    [PostgresFact]
    public async Task Concurrent_forfeits_abandon_the_match_once()
    {
        for (var round = 0; round < 10; round++)
        {
            var (room, host, bob, carol) = await StartedTrio();

            var results = await Task.WhenAll(new[] { host, bob, carol }.Select(player => Task.Run(() => Leave(player, room))));

            // The first two leave; the last finds the match already abandoned.
            Assert.Equal(2, results.Count(r => r.Outcome == RoomOutcome.Left));
            Assert.Single(results, r => r.Outcome == RoomOutcome.RoomClosed);
            var stored = await Stored(room);
            Assert.Equal((RoomStatus.Abandoned, 2), (stored.Status, stored.Members.Count(m => m.HasLeft)));
        }
    }

    private async Task SetStatus(GameRoom room, RoomStatus status)
    {
        await using var db = database.CreateDbContext();
        await RoomStatusOverride.SetAsync(db, room.Code, status, clock.GetUtcNow());
    }

    private async Task<(GameRoom Room, Player Host, Player Bob, Player Carol)> StartedTrio()
    {
        var host = await SavedGuest();
        var bob = await SavedGuest();
        var carol = await SavedGuest();
        var room = await SavedRoom(host, bob, carol);
        foreach (var player in new[] { host, bob, carol })
        {
            await Ready(player, room);
        }

        Assert.Equal(RoomOutcome.Started, (await Start(host, room)).Outcome);
        return (room, host, bob, carol);
    }

    [PostgresFact]
    public async Task A_leave_racing_a_start_either_is_refused_or_stops_the_start()
    {
        for (var round = 0; round < 10; round++)
        {
            var (room, host, bob) = await ReadyPair();

            var start = Task.Run(() => Start(host, room));
            var leave = Task.Run(() => Leave(bob, room));
            var (started, left) = (await start, await leave);

            var stored = await Stored(room);
            if (started.Outcome == RoomOutcome.Started)
            {
                // Leaving right after the start is a forfeit, which leaves one player: the match is abandoned (DEC-025).
                Assert.Equal(RoomOutcome.Left, left.Outcome);
                Assert.Equal((RoomStatus.Abandoned, 2), (stored.Status, stored.Members.Count));
                Assert.True(stored.HasLeft(bob.Id));
            }
            else
            {
                Assert.Equal(RoomOutcome.NotEnoughPlayers, started.Outcome);
                Assert.Equal(RoomOutcome.Left, left.Outcome);
                Assert.Equal((RoomStatus.Waiting, 1), (stored.Status, stored.Members.Count));
            }
        }
    }

    /// <summary>Joining never reads the catalogue; only creating a room does.</summary>
    private sealed class OpenCatalog : IGameCatalog
    {
        public GameCatalogItem? Find(string gameType) => throw new InvalidOperationException("Not used when joining.");
    }

    /// <summary>Chain stand-in: holdings per wallet, an outage switch and a record of live ownership checks.</summary>
    private sealed class FakeNftProvider : INftProvider
    {
        public ConcurrentDictionary<WalletAddress, List<OwnedNft>> Holdings { get; } = new();

        public ConcurrentQueue<(WalletAddress Wallet, string TokenId)> LiveChecks { get; } = new();

        public bool Down { get; set; }

        public Task<IReadOnlyList<OwnedNft>> GetOwnedAsync(WalletAddress owner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Starting a match checks single tokens only.");

        public Task<OwnedNft?> FindOwnedAsync(WalletAddress owner, NftTokenId tokenId, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new NftProviderUnavailableException("Chain is down.");
            }

            LiveChecks.Enqueue((owner, tokenId.ToString()));
            return Task.FromResult((Holdings.GetValueOrDefault(owner) ?? []).FirstOrDefault(n => n.TokenId == tokenId));
        }
    }
}
