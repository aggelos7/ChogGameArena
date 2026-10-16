using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Api.Matches;
using ChogGames.Games.ChogRace.Rules;
using ChogGames.Infrastructure.Matches;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogRace;

/// <summary>
/// STORY-050, DEC-054: a started Chog Race match carries the track and every pawn in each player's snapshot, from the
/// real rules. Pawns start on the start tile; the time cap isn't enforced until STORY-056.
/// </summary>
public sealed class ChogRaceBoardTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private ApiFactory _factory = null!;

    private sealed record MemberDto(string DisplayName, bool IsYou, bool HasLeft, bool IsTurn, int? Seat);

    private sealed record PawnDto(int Seat, int Tile);

    private sealed record TileDto(int Index, string Type, int? To);

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

        // The real Chog Race rules; the tests time out turns themselves.
        _factory = new ApiFactory(
            database.ConnectionString,
            new Dictionary<string, string?> { ["Games:Items:1:Status"] = "Available" },
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

    private static void DisposeAll(IEnumerable<HttpClient> players)
    {
        foreach (var player in players)
        {
            player.Dispose();
        }
    }

    private static async Task<RoomDto> ViewAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<RoomDto>($"/api/rooms/{code}", Json))!;

    private async Task<RoomDto> StartedAsync(params HttpClient[] players)
    {
        using var created = await players[0].PostAsJsonAsync("/api/rooms", new { gameType = "chog-race" });
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
    public async Task Every_player_sees_the_track_with_every_pawn_on_the_start_tile(int count)
    {
        var players = await GuestsAsync(count);
        try
        {
            var room = await StartedAsync(players);

            // Stored as jsonb, which orders keys by length. The deck, shuffled without reordering it here, stays on the server.
            var deck = string.Join(",", RaceRulesV1.Rules.Deck.Select(card => $"\"{card}\""));
            Assert.Equal(
                $$"""{"deck":[{{deck}}],"pawns":[""" + string.Join(",", Enumerable.Range(1, count).Select(s => $$"""{"seat":{{s}},"tile":0}""")) + """],"discard":[],"rulesVersion":1}""",
                (await StoredAsync(room)).State);
            // The 20-minute cap (STORY-056, DEC-060) runs from the start.
            Assert.Equal(("Active", (DateTimeOffset?)_clock.GetUtcNow().AddMinutes(20)), (room.Status, room.MatchEndsAt));
            foreach (var player in players)
            {
                var seen = await ViewAsync(player, room.Code);
                var board = seen.Board!;
                Assert.Equal(1, board.RulesVersion);
                Assert.Equal(28, board.Tiles.Length);
                Assert.Equal(Enumerable.Range(1, count).Select(seat => new PawnDto(seat, 0)), board.Pawns);
                Assert.Equal(1, seen.Members.Single(m => m.IsTurn).Seat);
            }

            var tiles = room.Board!.Tiles;
            Assert.Equal(new TileDto(0, "Start", null), tiles[0]);
            Assert.Equal(new TileDto(2, "Card", null), tiles[2]);
            Assert.Equal(new TileDto(12, "Boost", 15), tiles[12]);
            Assert.Equal(new TileDto(16, "Setback", 13), tiles[16]);
            Assert.Equal(new TileDto(27, "Finish", null), tiles[27]);
        }
        finally
        {
            DisposeAll(players);
        }
    }

    [PostgresFact]
    public async Task Pawns_of_players_who_left_are_taken_off_the_track()
    {
        var players = await GuestsAsync(3);
        try
        {
            var room = await StartedAsync(players);

            using var left = await players[1].PostAsync($"/api/rooms/{room.Code}/leave", null);
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);

            Assert.Equal([new PawnDto(1, 0), new PawnDto(3, 0)], (await ViewAsync(players[0], room.Code)).Board!.Pawns);
        }
        finally
        {
            DisposeAll(players);
        }
    }

    [PostgresFact]
    public async Task Outsiders_see_no_track_and_commands_move_no_pawn()
    {
        var players = await GuestsAsync(2);
        using var outsider = await GuestAsync("Mallory");
        try
        {
            var room = await StartedAsync(players);
            var before = await StoredAsync(room);

            using var peek = await outsider.GetAsync($"/api/rooms/{room.Code}");
            Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
            Assert.Equal("room_not_found", (await peek.Content.ReadFromJsonAsync<ProblemDto>(Json))!.Code);

            // An outsider can't roll, and a forged move to the finish is no command at all (STORY-051).
            using (var rolled = await outsider.PostAsJsonAsync(
                $"/api/rooms/{room.Code}/commands",
                new { actionId = Guid.NewGuid(), matchId = room.MatchId, expectedVersion = room.Version, type = "ROLL", payload = new { } }))
            {
                Assert.Equal(HttpStatusCode.NotFound, rolled.StatusCode);
            }

            using var moved = await players[0].PostAsJsonAsync(
                $"/api/rooms/{room.Code}/commands",
                new { actionId = Guid.NewGuid(), matchId = room.MatchId, expectedVersion = room.Version, type = "MOVE", payload = new { tile = 27 } });
            Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
            Assert.Equal("unknown_command", (await moved.Content.ReadFromJsonAsync<ProblemDto>(Json))!.Code);

            Assert.Equal(before, await StoredAsync(room));
        }
        finally
        {
            DisposeAll(players);
        }
    }

    [PostgresFact]
    public async Task A_turn_that_runs_out_is_skipped_and_leaves_the_pawns()
    {
        var players = await GuestsAsync(2);
        try
        {
            var room = await StartedAsync(players);
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
            DisposeAll(players);
        }
    }
}
