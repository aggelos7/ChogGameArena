using ChogGames.Core.Players;

namespace ChogGames.Infrastructure.Characters;

/// <summary>
/// The player's selected Chog as stored, for showing and checking their ability pool in the lobby (STORY-033, DEC-035).
/// Nothing is read from the chain and no traits are included; the shared pool doesn't read them. At start the pool is
/// checked again with the live snapshot (STORY-018), which is what the match keeps.
/// </summary>
public static class LobbyCharacters
{
    public static CharacterSnapshot For(Player player, DateTimeOffset now)
    {
        if (player.SelectedCharacterKind == CharacterKind.Nft)
        {
            return CharacterSnapshot.Restore(
                CharacterKind.Nft, player.SelectedCharacterId, $"Chog #{player.SelectedCharacterId}", null, [], player.WalletAddress, now);
        }

        var standard = DefaultAvatar.TryFind(player.SelectedCharacterId, out var found) ? found : DefaultAvatar.Classic;
        return CharacterSnapshot.Standard(standard, now);
    }
}
