using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Achievements;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Characters;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ChogGames.Api.Rooms.RoomEndpoints;

namespace ChogGames.Api.Rooms;

/// <summary>
/// What one member sees of a room (STORY-026, DEC-028): the same snapshot whether it is read over REST or pushed live,
/// tagged with the room's version so clients keep the newest. Player ids stay on the server; members are described only
/// by what the lobby shows. The game's state is shown only through its rules' per-player view (ENG-013), and never for a
/// game without rules. A member's ability choice is shown only to themselves until the start locks it (DEC-035); once
/// locked, everyone sees its uses left and each member's seat, which ability targets name (DEC-036). Achievements a
/// completed match unlocked are shown only to the player who earned them (DEC-063).
/// </summary>
public sealed class RoomSnapshots(
    ChogGamesDbContext db,
    IEnumerable<IMatchRules> rules,
    AbilityEngine abilityEngine,
    AbilityPool abilityPool,
    TimeProvider timeProvider)
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

        var unlocked = room.Status == RoomStatus.Completed
            ? (await db.PlayerAchievements.AsNoTracking()
                .Where(a => a.RoomId == room.Id && viewerIds.Contains(a.PlayerId))
                .Select(a => new { a.PlayerId, a.AchievementId })
                .ToListAsync(cancellationToken))
                .ToLookup(a => a.PlayerId, a => a.AchievementId)
            : null;

        using var state = room.State is not null && _rulesByGame.ContainsKey(room.GameType) ? JsonDocument.Parse(room.State) : null;
        return viewerIds.Distinct().ToDictionary(
            viewerId => viewerId,
            viewerId => Build(room, players, state, viewerId, unlocked?[viewerId] ?? []));
    }

    private RoomResponse Build(
        GameRoom room, Dictionary<Guid, Player> players, JsonDocument? state, Guid viewerId, IEnumerable<string> unlocked)
    {
        var members = room.Members
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.PlayerId != room.HostPlayerId)
            .Select(m =>
            {
                var player = players[m.PlayerId];
                var ability = AbilityOf(room, m);
                return new RoomMemberResponse(
                    player.DisplayName,
                    player.AvatarId,
                    m.PlayerId == room.HostPlayerId,
                    m.PlayerId == viewerId,
                    m.IsReadyWith(player),
                    m.Character is { } locked ? ToCharacter(locked) : ToCharacter(player),
                    room.Status == RoomStatus.Waiting && m.PlayerId != viewerId ? null : ability,
                    ability is not null,
                    m.HasLeft,
                    m.PlayerId == room.CurrentPlayerId,
                    m.Rank,
                    m.Seat,
                    ResultOf(m));
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
            room.TurnTimeout is { } turn ? (int)turn.TotalSeconds : null,
            room.MatchEndsAt,
            members,
            view,
            AbilityOptions(room, players, viewerId),
            [.. Achievement.All.Where(a => unlocked.Contains(a.Id)).Select(a => new RoomAchievementResponse(a.Id, a.Name, a.Description))]);
    }

    private static JsonElement? ResultOf(RoomMember member)
    {
        if (member.Result is null)
        {
            return null;
        }

        using var result = JsonDocument.Parse(member.Result);
        return result.RootElement.Clone();
    }

    /// <summary>
    /// The member's ability: the one locked at start, or their choice while waiting if the game still defines it. Who
    /// may see it is decided by the caller.
    /// </summary>
    private RoomAbilityResponse? AbilityOf(GameRoom room, RoomMember member)
    {
        if (member.Ability is { } locked)
        {
            return new RoomAbilityResponse(locked.Id, locked.Name, locked.Description, locked.Target, locked.MaxUses, member.AbilityUsesLeft);
        }

        return room.Status == RoomStatus.Waiting
            && member.AbilityId is { } chosen
            && abilityEngine.CatalogFor(room.GameType) is { } catalog
            && catalog.TryFind(chosen, out var definition, out _)
                ? ToAbility(definition)
                : null;
    }

    /// <summary>What the viewer may choose from while the room waits: their pool for the Chog they have selected.</summary>
    private RoomAbilityResponse[] AbilityOptions(GameRoom room, Dictionary<Guid, Player> players, Guid viewerId)
    {
        if (room.Status != RoomStatus.Waiting || !room.IsPlaying(viewerId) || !players.TryGetValue(viewerId, out var viewer))
        {
            return [];
        }

        return [.. abilityPool.For(room.GameType, LobbyCharacters.For(viewer, timeProvider.GetUtcNow())).Select(ToAbility)];
    }

    private static RoomAbilityResponse ToAbility(AbilityDefinition ability) =>
        new(ability.Id, ability.Name, ability.Description, ability.Target, ability.MaxUses);

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
