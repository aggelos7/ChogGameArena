using System.Text.Json;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ChogGames.Api.Rooms.RoomEndpoints;

namespace ChogGames.Api.Rooms;

/// <summary>
/// What one member sees of a room (STORY-026, DEC-028): the same snapshot whether it is read over REST or pushed live,
/// tagged with the room's version so clients keep the newest. Player ids stay on the server; members are described only
/// by what the lobby shows. The game's state is shown only through its rules' per-player view (ENG-013), and never for a
/// game without rules.
/// </summary>
public sealed class RoomSnapshots(ChogGamesDbContext db, IEnumerable<IMatchRules> rules)
{
    private readonly Dictionary<string, IMatchRules> _rulesByGame = rules.ToDictionary(r => r.GameType, StringComparer.Ordinal);

    public async Task<RoomResponse> ForAsync(GameRoom room, Guid viewerId, CancellationToken cancellationToken) =>
        (await ForEachAsync(room, [viewerId], cancellationToken))[viewerId];

    /// <summary>A snapshot for each of <paramref name="viewerIds"/>, all from the same reads, so they share one version.</summary>
    public async Task<IReadOnlyDictionary<Guid, RoomResponse>> ForEachAsync(
        GameRoom room, IReadOnlyCollection<Guid> viewerIds, CancellationToken cancellationToken)
    {
        var memberIds = room.Members.Select(m => m.PlayerId).ToArray();
        var players = await db.Players.AsNoTracking()
            .Where(p => memberIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        using var state = room.State is not null && _rulesByGame.ContainsKey(room.GameType) ? JsonDocument.Parse(room.State) : null;
        return viewerIds.Distinct().ToDictionary(viewerId => viewerId, viewerId => Build(room, players, state, viewerId));
    }

    private RoomResponse Build(GameRoom room, Dictionary<Guid, Player> players, JsonDocument? state, Guid viewerId)
    {
        var members = room.Members
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.PlayerId != room.HostPlayerId)
            .Select(m =>
            {
                var player = players[m.PlayerId];
                return new RoomMemberResponse(
                    player.DisplayName,
                    player.AvatarId,
                    m.PlayerId == room.HostPlayerId,
                    m.PlayerId == viewerId,
                    m.IsReadyWith(player),
                    m.Character is { } locked ? ToCharacter(locked) : ToCharacter(player),
                    Ability: null,
                    m.HasLeft,
                    m.PlayerId == room.CurrentPlayerId);
            })
            .ToArray();

        // Cloned: the view may point into the parsed document, which is disposed once every snapshot is built.
        var view = state is null ? null : _rulesByGame[room.GameType].ViewFor(new MatchViewContext(room, viewerId, state.RootElement))?.Clone();

        return new RoomResponse(
            room.Code,
            room.Id,
            room.Version,
            room.GameType,
            room.Status,
            room.MinPlayers,
            room.Capacity,
            room.TurnNumber,
            room.TurnEndsAt,
            members,
            view);
    }

    /// <summary>
    /// The selected Chog as stored; ownership is checked when the host starts, not here. A retired standard Chog
    /// shows as the default, as at start.
    /// </summary>
    private static RoomCharacterResponse ToCharacter(Player player)
    {
        if (player.SelectedCharacterKind == CharacterKind.Nft)
        {
            return new RoomCharacterResponse(CharacterKind.Nft, player.SelectedCharacterId, $"Chog #{player.SelectedCharacterId}", null);
        }

        var standard = DefaultAvatar.TryFind(player.SelectedCharacterId, out var found) ? found : DefaultAvatar.Classic;
        return new RoomCharacterResponse(CharacterKind.Standard, standard.Id, standard.Name, standard.Color);
    }

    /// <summary>The Chog locked when the match started, with its stored name.</summary>
    private static RoomCharacterResponse ToCharacter(CharacterSnapshot snapshot) =>
        new(
            snapshot.Kind,
            snapshot.CharacterId,
            snapshot.Name,
            snapshot.Kind == CharacterKind.Standard && DefaultAvatar.TryFind(snapshot.CharacterId, out var standard) ? standard.Color : null);
}
