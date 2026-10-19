using ChogGames.Core.Players;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Legal;

/// <summary>
/// What the terms of use and privacy pages need from the server (STORY-069, DEC-070): the current terms version, which
/// players accept before uploading a photo, and the deployment's contact address. Public, like the pages.
/// </summary>
public static class LegalEndpoints
{
    /// <param name="ContactEmail">Null when the deployment set none, which happens only outside production.</param>
    public sealed record LegalResponse(string TermsVersion, string? ContactEmail);

    public static IServiceCollection AddLegal(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LegalOptions>()
            .Bind(configuration.GetSection(LegalOptions.SectionName))
            .Validate(o => o.IsValid(), $"Legal:ContactEmail must be an email address of at most {LegalOptions.MaxContactEmailLength} characters.")
            .ValidateOnStart();
        return services;
    }

    public static IEndpointRouteBuilder MapLegalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/legal", (IOptions<LegalOptions> options) => TypedResults.Ok(new LegalResponse(
                TermsOfUse.CurrentVersion,
                options.Value.ContactEmail is { Length: > 0 } email ? email : null)))
            .AllowAnonymous()
            .WithName("GetLegal");
        return endpoints;
    }
}
