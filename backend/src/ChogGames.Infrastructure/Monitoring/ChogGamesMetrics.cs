using System.Diagnostics.Metrics;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Wallets;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Infrastructure.Monitoring;

/// <summary>
/// Game and session metrics (STORY-048, DEC-052), on the <see cref="MeterName"/> meter. Tags are low-cardinality labels
/// only: a game id, an outcome or a result. Never a player id, wallet address, room code, token or signature.
/// </summary>
public sealed class ChogGamesMetrics
{
    public const string MeterName = "ChogGames";

    private readonly Counter<long> _roomsCreated;
    private readonly Counter<long> _roomsCancelled;
    private readonly Counter<long> _matchesStarted;
    private readonly Counter<long> _matchesEnded;
    private readonly Counter<long> _guestSessionsCreated;
    private readonly Counter<long> _walletSignIns;
    private readonly Counter<long> _roomSubscriptions;

    // Open rooms by game and status, as last counted by RefreshOpenRoomsAsync; read when the gauge is collected.
    private volatile IReadOnlyList<Measurement<long>> _openRooms = [];
    private readonly HashSet<string> _gamesSeen = new(StringComparer.Ordinal);

    public ChogGamesMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _roomsCreated = meter.CreateCounter<long>("choggames.rooms.created", "{room}", "Rooms created.");
        _roomsCancelled = meter.CreateCounter<long>("choggames.rooms.cancelled", "{room}", "Rooms the host closed before the match started.");
        _matchesStarted = meter.CreateCounter<long>("choggames.matches.started", "{match}", "Matches started.");
        _matchesEnded = meter.CreateCounter<long>(
            "choggames.matches.ended", "{match}", "Matches ended: completed by the game, or abandoned when fewer than two players remained.");
        _guestSessionsCreated = meter.CreateCounter<long>("choggames.guest_sessions.created", "{session}", "Guest players created.");
        _walletSignIns = meter.CreateCounter<long>("choggames.wallet.sign_ins", "{attempt}", "Wallet sign-in verifications, by result.");
        _roomSubscriptions = meter.CreateCounter<long>(
            "choggames.realtime.room_subscriptions", "{subscription}", "Live room subscriptions, by result; every (re)connect subscribes again.");
        meter.CreateObservableGauge("choggames.rooms.open", () => _openRooms, "{room}", "Rooms waiting for players or playing a match.");
    }

    /// <summary>Records committed room status changes; <see cref="RoomLifecycleMetricsInterceptor"/> calls it.</summary>
    public void RoomChanged(string gameType, RoomStatus? from, RoomStatus to)
    {
        var game = new KeyValuePair<string, object?>("game", gameType);
        switch (from, to)
        {
            case (null, RoomStatus.Waiting):
                _roomsCreated.Add(1, game);
                break;
            case (RoomStatus.Waiting, RoomStatus.Cancelled):
                _roomsCancelled.Add(1, game);
                break;
            case (RoomStatus.Waiting, RoomStatus.Active):
                _matchesStarted.Add(1, game);
                break;
            case (RoomStatus.Active, RoomStatus.Completed):
                _matchesEnded.Add(1, game, new KeyValuePair<string, object?>("outcome", "completed"));
                break;
            case (RoomStatus.Active, RoomStatus.Abandoned):
                _matchesEnded.Add(1, game, new KeyValuePair<string, object?>("outcome", "abandoned"));
                break;
        }
    }

    public void GuestSessionCreated() => _guestSessionsCreated.Add(1);

    public void WalletSignIn(WalletSignInFailure? failure) =>
        _walletSignIns.Add(1, new KeyValuePair<string, object?>("result", failure switch
        {
            null => "succeeded",
            WalletSignInFailure.ChallengeUnavailable => "challenge_unavailable",
            WalletSignInFailure.InvalidSignature => "invalid_signature",
            _ => "failed",
        }));

    public void RoomSubscription(bool subscribed) =>
        _roomSubscriptions.Add(1, new KeyValuePair<string, object?>("result", subscribed ? "subscribed" : "room_not_found"));

    /// <summary>
    /// Counts the open rooms for the <c>choggames.rooms.open</c> gauge. Run before each collection: the count lives in the
    /// database, so it is right across restarts and API instances.
    /// </summary>
    public async Task RefreshOpenRoomsAsync(ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        // Left out until counted again, so a failed count never shows a stale value.
        _openRooms = [];
        var counts = await db.GameRooms
            .Where(r => r.Status == RoomStatus.Waiting || r.Status == RoomStatus.Active)
            .GroupBy(r => new { r.GameType, r.Status })
            .Select(g => new { g.Key.GameType, g.Key.Status, Count = g.LongCount() })
            .ToListAsync(cancellationToken);

        string[] gamesSeen;
        // Every game seen since the start gets both statuses, so a series falls to 0 instead of disappearing.
        lock (_gamesSeen)
        {
            _gamesSeen.UnionWith(counts.Select(c => c.GameType));
            gamesSeen = [.. _gamesSeen.Order(StringComparer.Ordinal)];
        }

        _openRooms = gamesSeen
            .SelectMany(game => new[] { RoomStatus.Waiting, RoomStatus.Active }.Select(status => new Measurement<long>(
                counts.Where(c => c.GameType == game && c.Status == status).Sum(c => c.Count),
                new KeyValuePair<string, object?>("game", game),
                new KeyValuePair<string, object?>("status", status == RoomStatus.Waiting ? "waiting" : "active"))))
            .ToList();
    }
}
