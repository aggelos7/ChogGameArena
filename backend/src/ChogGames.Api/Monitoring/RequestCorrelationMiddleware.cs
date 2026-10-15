using System.Text.RegularExpressions;

namespace ChogGames.Api.Monitoring;

/// <summary>
/// Request correlation (STORY-048, DEC-052). Every request gets a correlation id: the <see cref="HeaderName"/> header
/// nginx sets (its <c>$request_id</c>, also in its access log), or a new one when it is missing or not a plain token.
/// The id is returned in the same response header and is on every log line written while the request runs, as the
/// <see cref="ScopeKey"/> scope value, so a user's report, the proxy log and the API log can be joined.
/// </summary>
public sealed partial class RequestCorrelationMiddleware(RequestDelegate next, ILogger<RequestCorrelationMiddleware> logger)
{
    public const string HeaderName = "X-Request-Id";
    public const string ScopeKey = "CorrelationId";

    // Letters, digits, '-', '_' and '.', up to 64: a client's value can't forge log lines or blow up their size.
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidId();

    public async Task InvokeAsync(HttpContext context)
    {
        var sent = context.Request.Headers[HeaderName];
        var id = sent.Count == 1 && ValidId().IsMatch(sent[0]!) ? sent[0]! : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = id;
        context.Response.Headers[HeaderName] = id;
        using (logger.BeginScope(new Dictionary<string, object> { [ScopeKey] = id }))
        {
            await next(context);
        }
    }
}
