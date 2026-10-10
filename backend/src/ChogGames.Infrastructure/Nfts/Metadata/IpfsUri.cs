using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ChogGames.Infrastructure.Nfts.Metadata;

/// <summary>
/// A content-addressed <c>ipfs://&lt;cid&gt;/&lt;path&gt;</c> reference. Only this form is accepted from on-chain
/// or metadata URIs, so a collection can never point the server or browsers at an arbitrary host; it is turned
/// into an HTTP URL only with a configured gateway.
/// </summary>
public sealed partial record IpfsUri
{
    public const int MaxLength = 512;

    private IpfsUri(string cid, string path)
    {
        Cid = cid;
        Path = path;
    }

    public string Cid { get; }

    /// <summary>Slash-separated path below the CID, possibly empty.</summary>
    public string Path { get; }

    public static bool TryParse(string? input, [NotNullWhen(true)] out IpfsUri? uri)
    {
        uri = null;
        if (input is null || input.Length > MaxLength || !input.StartsWith("ipfs://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = input["ipfs://".Length..];
        // Legacy "ipfs://ipfs/<cid>" form.
        if (rest.StartsWith("ipfs/", StringComparison.Ordinal))
        {
            rest = rest["ipfs/".Length..];
        }

        var slash = rest.IndexOf('/');
        var cid = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? string.Empty : rest[(slash + 1)..];
        if (!CidPattern().IsMatch(cid) || (path.Length > 0 && !PathPattern().IsMatch(path)))
        {
            return false;
        }

        uri = new IpfsUri(cid, path);
        return true;
    }

    /// <summary>HTTP URL through a gateway base such as <c>https://gateway.example/ipfs/</c>.</summary>
    public Uri ToGatewayUrl(string gatewayBase) =>
        new(gatewayBase + Cid + (Path.Length == 0 ? string.Empty : "/" + Path));

    public override string ToString() => "ipfs://" + Cid + (Path.Length == 0 ? string.Empty : "/" + Path);

    // CIDv0 (base58btc "Qm…") or CIDv1 (base32/base36 multibase, lowercase).
    [GeneratedRegex("^(Qm[1-9A-HJ-NP-Za-km-z]{44}|b[a-z2-7]{20,100}|k[0-9a-z]{20,100})$")]
    private static partial Regex CidPattern();

    // Plain segments only: no empty, "." or ".." segments, query, fragment or percent-encoding.
    [GeneratedRegex(@"^(?!.*(^|/)\.{1,2}(/|$))[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex PathPattern();
}
