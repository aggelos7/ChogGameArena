using System.Net;
using ChogGames.Api.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Tests.Monitoring;

/// <summary>Metrics, request correlation and log format without a database (STORY-048).</summary>
public sealed class MonitoringEndpointTests : IAsyncLifetime
{
    private readonly CapturingLoggerProvider _logs = new();
    private ApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(ApiFactory.UnusedDatabase, configureServices: _logs.AddTo);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<HttpResponseMessage> GetAsync(string path, string? requestId = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (requestId is not null)
        {
            request.Headers.TryAddWithoutValidation(RequestCorrelationMiddleware.HeaderName, requestId);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Metrics_are_served_in_the_prometheus_text_format_with_request_durations_by_route()
    {
        using var live = await GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        using var response = await GetAsync(MonitoringEndpoints.MetricsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PrometheusMetrics.ContentType, response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE http_server_request_duration_seconds histogram", text);
        Assert.Contains(
            "http_server_request_duration_seconds_count{http_request_method=\"GET\",http_response_status_code=\"200\",http_route=\"/health/live\"} 1",
            text);
    }

    [Fact]
    public async Task Metrics_are_still_served_without_the_database_leaving_open_rooms_out()
    {
        using var response = await GetAsync(MonitoringEndpoints.MetricsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("choggames_rooms_open", await response.Content.ReadAsStringAsync());
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message == "Open rooms could not be counted for the metrics.");
    }

    [Fact]
    public async Task A_valid_request_id_is_kept_returned_and_on_every_log_line_of_the_request()
    {
        using var response = await GetAsync("/health/live", "abc-123_DEF.4");

        Assert.Equal("abc-123_DEF.4", Assert.Single(response.Headers.GetValues(RequestCorrelationMiddleware.HeaderName)));
        var endpointLog = Assert.Single(_logs.Entries, e => e.Message.StartsWith("Executing endpoint", StringComparison.Ordinal));
        Assert.Contains(new KeyValuePair<string, object?>(RequestCorrelationMiddleware.ScopeKey, "abc-123_DEF.4"), endpointLog.Scopes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("forged\" level=\"Error")]
    [InlineData("an-id-that-is-far-too-long-to-be-kept-0123456789-0123456789-0123456789")]
    public async Task A_missing_or_unusable_request_id_is_replaced_by_a_new_one(string? sent)
    {
        using var response = await GetAsync("/health/live", sent);

        var id = Assert.Single(response.Headers.GetValues(RequestCorrelationMiddleware.HeaderName));
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.DoesNotContain(_logs.Entries, e => sent is { Length: > 0 } && e.Text.Contains(sent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_request_gets_its_own_id()
    {
        using var first = await GetAsync("/health/live");
        using var second = await GetAsync("/health/live");

        Assert.NotEqual(
            first.Headers.GetValues(RequestCorrelationMiddleware.HeaderName).Single(),
            second.Headers.GetValues(RequestCorrelationMiddleware.HeaderName).Single());
    }

    [Fact]
    public void Console_logs_are_json_lines_with_scopes_and_utc_timestamps_outside_development()
    {
        var services = _factory.Services;

        Assert.Equal(ConsoleFormatterNames.Json, services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
        var json = services.GetRequiredService<IOptionsMonitor<JsonConsoleFormatterOptions>>().CurrentValue;
        Assert.True(json.IncludeScopes);
        Assert.True(json.UseUtcTimestamp);
        Assert.Equal("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", json.TimestampFormat);
    }
}
