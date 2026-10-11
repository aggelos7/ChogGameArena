using System.Security.Cryptography;
using ChogGames.Core.Rooms;

namespace ChogGames.Infrastructure.Rooms;

/// <summary>Room codes from a cryptographically secure source, so they cannot be predicted (ENG-008).</summary>
public sealed class SecureRoomCodeGenerator : IRoomCodeGenerator
{
    public RoomCode Next()
    {
        RoomCode.TryParse(RandomNumberGenerator.GetString(RoomCode.Alphabet, RoomCode.Length), out var code);
        return code;
    }
}
