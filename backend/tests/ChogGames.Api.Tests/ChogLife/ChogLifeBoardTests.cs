using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Api.Matches;
using ChogGames.Games.ChogLife.Rules;
using ChogGames.Infrastructure.Matches;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogLife;

/// <summary>
/// STORY-036, DEC-038: a started Chog Life match carries the board and every pawn in each player's snapshot, from the
/// real rules. Pawns start on the start tile and nothing moves them yet.
/// </summary>
public sealed class ChogLifeBoardTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private ApiFactory _factory = null!;

    private sealed record MemberDto(string DisplayName, bool IsYou, bool HasLeft, bool IsTurn, int? Seat);

    private sealed record PawnDto(int Seat, int Tile);

    private sealed record TileDto(int Index, string Type, string Lane, int? Next, int? SafeNext, int? DegenNext, int? Amount, string? Event);

    private sealed record BoardDto(int RulesVersion, TileDto[] Tiles, PawnDto[] Pawns);

    private sealed record RoomDto(
        string Code, Guid MatchId, long Version, string GameType, string Status, MemberDto[] Members, JsonElement? State, DateTimeOffset? MatchEndsAt = null)
    {
        public BoardDto? Board => State is { ValueKind: JsonValueKind.Object } state ? state.Deserialize<BoardDto>(Json) : null;
    }

    private sealed record ProblemDto(int Status, string? Code, string? Reason);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        // The real Chog Life rules; the tests time out turns themselves.
        _factory = new ApiFactory(
            database.ConnectionString,
            new Dictionary<string, string?>
            {
                ["Games:Items:0:Status"] = "Available",
                ["Games:Items:1:Status"] = "Available",
            },
            services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                foreach (var worker in services.Where(d => d.ImplementationType == typeof(TurnTimeoutWorker)).ToList())
                {
                    services.Remove(worker);
                }
            });
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<HttpClient> GuestAsync(string name)
    {
        var client = _factory.CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return client;
    }

    private async Task<HttpClient[]> GuestsAsync(int count)
    {
        var guests = new HttpClient[count];
        for (var i = 0; i < count; i++)
        {
            guests[i] = await GuestAsync($"Player {i + 1}");
        }

        return guests;
    }

    private static async Task<RoomDto> ViewAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<RoomDto>($"/api/rooms/{code}", Json))!;

    private async Task<RoomDto> WaitingAsync(string gameType, params HttpClient[] players)
    {
        using var created = await players[0].PostAsJsonAsync("/api/rooms", new { gameType });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = (await created.Content.ReadFromJsonAsync<RoomDto>(Json))!;
        foreach (var player in players[1..])
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            using var joined = await player.PostAsync($"/api/rooms/{room.Code}/join", null);
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }

        foreach (var player in players)
        {
            using var ready = await player.PutAsJsonAsync($"/api/rooms/{room.Code}/ready", new { ready = true });
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        return await ViewAsync(players[0], room.Code);
    }

    private async Task<RoomDto> StartedAsync(string gameType, params HttpClient[] players)
    {
        var room = await WaitingAsync(gameType, players);
        using var started = await players[0].PostAsync($"/api/rooms/{room.Code}/start", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        return await ViewAsync(players[0], room.Code);
    }

    private async Task<(long Version, string? State)> StoredAsync(RoomDto room)
    {
        await using var db = database.CreateDbContext();
        var stored = await db.GameRooms.AsNoTracking().SingleAsync(r => r.Id == room.MatchId);
        return (stored.Version, stored.State is null ? null : JsonSerializer.Serialize(JsonDocument.Parse(stored.State).RootElement));
    }

    [PostgresTheory]
    [InlineData(2)]
    [InlineData(6)]
    public async Task Every_player_sees_the_board_with_every_pawn_on_the_start_tile(int count)
    {
        var players = await GuestsAsync(count);
        try
        {
            var room = await StartedAsync("chog-life", players);

            // Stored as jsonb, which orders keys by length.
            Assert.Equal(
                """{"pawns":[""" + string.Join(",", Enumerable.Range(1, count).Select(s => $$"""{"mon":100,"seat":{{s}},"tile":0}""")) + """],"rulesVersion":2}""",
                (await StoredAsync(room)).State);
            foreach (var player in players)
            {
                var seen = await ViewAsync(player, room.Code);
                var board = seen.Board!;
                Assert.Equal(LifeRulesV2.Version, board.RulesVersion);
                Assert.Equal(36, board.Tiles.Length);
                Assert.Equal(Enumerable.Range(1, count).Select(seat => new PawnDto(seat, 0)), board.Pawns);
                Assert.Equal(Enumerable.Range(1, count).Select(seat => (int?)seat), seen.Members.Select(m => m.Seat).Order());
                Assert.Equal(1, seen.Members.Single(m => m.IsTurn).Seat);
            }

            var tiles = room.Board!.Tiles;
            Assert.Equal(new TileDto(0, "Start", "Main", 1, null, null, null, null), tiles[0]);
            Assert.Equal(new TileDto(9, "Branch", "Main", null, 10, 14, null, null), tiles[9]);
            Assert.Equal(new TileDto(15, "Reward", "Degen", 16, null, null, 60, null), tiles[15]);
            Assert.Equal(new TileDto(29, "Event", "Degen", 30, null, null, null, "Liquidation"), tiles[29]);
            Assert.Equal(new TileDto(35, "Finish", "Main", null, null, null, null, null), tiles[35]);
        }
        finally
        {
            foreach (var player in players)
            {
                player.Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task A_waiting_room_and_a_game_without_rules_show_no_board_or_time_limit()
    {
        var players = await GuestsAsync(4);
        try
        {
            var waiting = await WaitingAsync("chog-life", players[0], players[1]);
            Assert.Equal(((JsonElement?)null, (DateTimeOffset?)null), (waiting.State, waiting.MatchEndsAt));

            var race = await StartedAsync("chog-race", players[2], players[3]);
            Assert.Equal("Active", race.Status);
            Assert.Equal(((JsonElement?)null, (DateTimeOffset?)null), (race.State, race.MatchEndsAt));
            Assert.Null((await StoredAsync(race)).State);
        }
        finally
        {
            foreach (var player in players)
            {
                player.Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task Pawns_of_players_who_left_are_taken_off_the_board()
    {
        var players = await GuestsAsync(3);
        try
        {
            var room = await StartedAsync("chog-life", players);

            using var left = await players[1].PostAsync($"/api/rooms/{room.Code}/leave", null);
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);

            var seen = await ViewAsync(players[0], room.Code);
            Assert.Equal([new PawnDto(1, 0), new PawnDto(3, 0)], seen.Board!.Pawns);
            Assert.Equal(36, seen.Board.Tiles.Length);
        }
        finally
        {
            foreach (var player in players)
            {
                player.Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task Outsiders_see_no_board_and_commands_move_no_pawn()
    {
        var players = await GuestsAsync(2);
        using var outsider = await GuestAsync("Mallory");
        try
        {
            var room = await StartedAsync("chog-life", players);
            var before = await StoredAsync(room);

            using var peek = await outsider.GetAsync($"/api/rooms/{room.Code}");
            Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
            Assert.Equal("room_not_found", (await peek.Content.ReadFromJsonAsync<ProblemDto>(Json))!.Code);

            // Only ROLL moves a pawn (STORY-037): a forged move straight to the finish is an unknown command.
            using var moved = await players[0].PostAsJsonAsync(
                $"/api/rooms/{room.Code}/commands",
                new { actionId = Guid.NewGuid(), matchId = room.MatchId, expectedVersion = room.Version, type = "MOVE", payload = new { tile = 35 } });
            Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
            Assert.Equal("unknown_command", (await moved.Content.ReadFromJsonAsync<ProblemDto>(Json))!.Code);

            Assert.Equal(before, await StoredAsync(room));
        }
        finally
        {
            foreach (var player in players)
            {
                player.Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task A_turn_that_runs_out_is_skipped_and_leaves_the_pawns()
    {
        var players = await GuestsAsync(2);
        try
        {
            var room = await StartedAsync("chog-life", players);
            var before = await StoredAsync(room);

            _clock.Advance(TimeSpan.FromMinutes(1));
            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var timedOut = await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None);
                Assert.Single(timedOut);
            }

            var seen = await ViewAsync(players[1], room.Code);
            Assert.Equal(2, seen.Members.Single(m => m.IsTurn).Seat);
            Assert.Equal([new PawnDto(1, 0), new PawnDto(2, 0)], seen.Board!.Pawns);
            Assert.Equal(before.State, (await StoredAsync(room)).State);
        }
        finally
        {
            foreach (var player in players)
            {
                player.Dispose();
            }
        }
    }
}
