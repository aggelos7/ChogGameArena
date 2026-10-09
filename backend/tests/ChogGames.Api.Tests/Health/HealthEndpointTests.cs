using System.Net;
using System.Text.Json;

namespace ChogGames.Api.Tests.Health;

public class HealthEndpointTests
{
    // Nothing listens on port 1, so connections are refused immediately.
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=choggames;Username=choggames;Password=unreachable-secret;Timeout=2";

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(string connectionString, string path)
    {
        await using var factory = new ApiFactory(connectionString);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task Liveness_is_healthy_when_database_is_unavailable()
    {
        var (status, body) = await GetAsync(UnreachableDatabase, "/health/live");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", Json(body).GetProperty("status").GetString());
        Assert.Empty(Json(body).GetProperty("checks").EnumerateArray());
    }

    [Fact]
    public async Task Readiness_fails_when_database_is_unavailable()
    {
        var (status, body) = await GetAsync(UnreachableDatabase, "/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", Json(body).GetProperty("status").GetString());
        var check = Assert.Single(Json(body).GetProperty("checks").EnumerateArray());
        Assert.Equal("postgresql", check.GetProperty("name").GetString());
        Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_failure_does_not_expose_connection_details()
    {
        var (_, body) = await GetAsync(UnreachableDatabase, "/health/ready");

        Assert.DoesNotContain("unreachable-secret", body);
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("Exception", body);
    }
}
