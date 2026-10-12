using System.Security.Claims;
using System.Threading.RateLimiting;
using ChogGames.Api.Realtime;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Nfts;
using ChogGames.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Characters;

/// <summary>
/// The caller's character gallery (STORY-017, NFT-002/004/005/006): verified Chog NFTs of the signed-in wallet and
/// the standard Chogs everyone can use, plus the one preferred character. The token list always comes from the
/// server's ownership check; nothing the browser sends about ownership is trusted.
/// </summary>
public static class CharacterEndpoints
{
    public const string RateLimitPolicy = "characters";

    public sealed record TraitResponse(string Type, string Value);

    public sealed record NftCharacterResponse(string TokenId, string Name, string? ImageUrl, TraitResponse[] Traits);

    public sealed record StandardCharacterResponse(string Id, string Name, string Color);

    public sealed record SelectedCharacterResponse(CharacterKind Kind, string Id);

    public sealed record CharactersResponse(
        NftGalleryStatus NftStatus,
        NftCharacterResponse[] Nfts,
        StandardCharacterResponse[] Standard,
        SelectedCharacterResponse Selected);

    public sealed record SelectCharacterRequest(string? Kind, string? Id);

    public enum NftGalleryStatus
    {
        /// <summary>The wallet's NFTs were read; the list may be empty.</summary>
        Available,

        /// <summary>A guest: NFTs need a verified wallet sign-in.</summary>
        NoWallet,

        /// <summary>Ownership could not be read right now; standard Chogs are still offered.</summary>
        Unavailable,
    }

    public static IServiceCollection AddCharacters(this IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, context =>
        {
            // Partitioned per player, since every request may read the chain for that player's wallet.
            var permitLimit = context.RequestServices.GetRequiredService<IOptions<NftOptions>>().Value.RequestsPerMinutePerPlayer;
            return RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        }));

        return services;
    }

    public static IEndpointRouteBuilder MapCharacterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var characters = endpoints.MapGroup("/api/characters").RequireAuthorization().RequireRateLimiting(RateLimitPolicy);

        characters.MapGet("", GetCharactersAsync).WithName("GetCharacters");
        characters.MapPut("/selected", SelectCharacterAsync).WithName("SelectCharacter");

        return endpoints;
    }

    private static async Task<Results<Ok<CharactersResponse>, NotFound>> GetCharactersAsync(
        ClaimsPrincipal user, ChogGamesDbContext db, INftProvider nfts, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        var status = NftGalleryStatus.NoWallet;
        IReadOnlyList<OwnedNft> owned = [];
        if (WalletAddress.TryParse(player.WalletAddress, out var wallet))
        {
            try
            {
                owned = await nfts.GetOwnedAsync(wallet, cancellationToken);
                status = NftGalleryStatus.Available;
            }
            catch (NftProviderUnavailableException)
            {
                loggers.CreateLogger(typeof(CharacterEndpoints)).LogWarning("NFT gallery shown without NFTs: ownership is unavailable.");
                status = NftGalleryStatus.Unavailable;
            }
        }

        return TypedResults.Ok(new CharactersResponse(
            status,
            owned.Select(ToResponse).ToArray(),
            DefaultAvatar.All.Select(ToResponse).ToArray(),
            EffectiveSelection(player, status, owned)));
    }

    private static async Task<Results<Ok<SelectedCharacterResponse>, ValidationProblem, NotFound, ProblemHttpResult>> SelectCharacterAsync(
        SelectCharacterRequest? request,
        ClaimsPrincipal user,
        ChogGamesDbContext db,
        INftProvider nfts,
        RoomNotifier notifier,
        CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        // Kinds are matched exactly, so numeric enum values and other spellings are refused.
        switch (request?.Kind)
        {
            case nameof(CharacterKind.Standard):
                if (!DefaultAvatar.TryFind(request.Id, out var standard))
                {
                    return Invalid("id", $"Unknown standard Chog. Choose one of: {string.Join(", ", DefaultAvatar.All.Select(a => a.Id))}.");
                }

                player.SelectStandardCharacter(standard);
                break;

            case nameof(CharacterKind.Nft):
                if (!WalletAddress.TryParse(player.WalletAddress, out var wallet))
                {
                    return Invalid("kind", "Sign in with a wallet to choose a Chog NFT.");
                }

                if (!NftTokenId.TryParse(request.Id, out var tokenId))
                {
                    return Invalid("id", "Token id must be a whole number without leading zeros.");
                }

                OwnedNft? nft;
                try
                {
                    // Always a live check against the chain, never the browser's list or stored ownership (NFT-008).
                    nft = await nfts.FindOwnedAsync(wallet, tokenId, cancellationToken);
                }
                catch (NftProviderUnavailableException)
                {
                    return TypedResults.Problem(
                        "NFT ownership cannot be checked right now. Try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                if (nft is null)
                {
                    return Invalid("id", "Your wallet does not own this Chog.");
                }

                player.SelectNftCharacter(nft.TokenId);
                break;

            default:
                return Invalid("kind", "Kind must be Standard or Nft.");
        }

        await db.SaveChangesAsync(cancellationToken);

        // The lobby shows the new Chog, and a ready member is no longer ready with it (DEC-022).
        var openRoomCode = await db.GameRooms.AsNoTracking()
            .Where(r => r.Status == RoomStatus.Waiting && r.Members.Any(m => m.PlayerId == player.Id))
            .Select(r => r.Code)
            .FirstOrDefaultAsync(cancellationToken);
        if (openRoomCode is not null)
        {
            await notifier.RoomChangedAsync(openRoomCode);
        }

        return TypedResults.Ok(new SelectedCharacterResponse(player.SelectedCharacterKind, player.SelectedCharacterId));
    }

    /// <summary>
    /// The stored selection, unless it is an NFT the wallet was just shown not to own any more; then the classic
    /// standard Chog. When ownership could not be read, the stored choice is kept as is.
    /// </summary>
    private static SelectedCharacterResponse EffectiveSelection(Player player, NftGalleryStatus status, IReadOnlyList<OwnedNft> owned)
    {
        if (player.SelectedCharacterKind == CharacterKind.Nft)
        {
            var stillOwned = status == NftGalleryStatus.Available && owned.Any(n => n.TokenId.ToString() == player.SelectedCharacterId);
            if (!stillOwned && status != NftGalleryStatus.Unavailable)
            {
                return new SelectedCharacterResponse(CharacterKind.Standard, DefaultAvatar.Classic.Id);
            }
        }
        else if (!DefaultAvatar.TryFind(player.SelectedCharacterId, out _))
        {
            // A standard Chog retired from the built-in list falls back to the default rather than failing.
            return new SelectedCharacterResponse(CharacterKind.Standard, DefaultAvatar.Classic.Id);
        }

        return new SelectedCharacterResponse(player.SelectedCharacterKind, player.SelectedCharacterId);
    }

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static Task<Player?> FindCurrentPlayerAsync(ClaimsPrincipal user, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var playerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.Players.SingleOrDefaultAsync(p => p.Id == playerId, cancellationToken);
    }

    private static NftCharacterResponse ToResponse(OwnedNft nft) => new(
        nft.TokenId.ToString(),
        nft.Name,
        nft.ImageUrl?.ToString(),
        nft.Traits.Select(t => new TraitResponse(t.Type, t.Value)).ToArray());

    private static StandardCharacterResponse ToResponse(DefaultAvatar avatar) => new(avatar.Id, avatar.Name, avatar.Color);
}
