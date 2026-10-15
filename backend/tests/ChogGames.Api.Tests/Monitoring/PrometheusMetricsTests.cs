using System.Diagnostics.Metrics;
using ChogGames.Api.Monitoring;
using ChogGames.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Monitoring;

public sealed class PrometheusMetricsTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly PrometheusMetrics _prometheus;

    public PrometheusMetricsTests() => _prometheus = new PrometheusMetrics(Factory);

    private IMeterFactory Factory => _services.GetRequiredService<IMeterFactory>();

    public void Dispose()
    {
        _prometheus.Dispose();
        _services.Dispose();
    }

    private string[] Lines() => _prometheus.Collect().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Counters_are_cumulative_totals_per_label_set_with_help_and_type()
    {
        var counter = Factory.Create(ChogGamesMetrics.MeterName).CreateCounter<long>("choggames.rooms.created", "{room}", "Rooms created.");

        counter.Add(1, new KeyValuePair<string, object?>("game", "chog-life"));
        counter.Add(2, new KeyValuePair<string, object?>("game", "chog-life"));
        counter.Add(1, new KeyValuePair<string, object?>("game", "chog-race"));

        Assert.Equal(
            [
                "# HELP choggames_rooms_created_total Rooms created.",
                "# TYPE choggames_rooms_created_total counter",
                "choggames_rooms_created_total{game=\"chog-life\"} 3",
                "choggames_rooms_created_total{game=\"chog-race\"} 1",
            ],
            Lines());
    }

    [Fact]
    public void Histograms_have_cumulative_buckets_sum_and_count_in_seconds()
    {
        var histogram = Factory.Create("Microsoft.AspNetCore.Hosting").CreateHistogram<double>("http.server.request.duration", "s");
        var tags = new KeyValuePair<string, object?>[] { new("http.route", "/api/me"), new("http.request.method", "GET") };

        // Exact in binary, so the sum is too. A value on a boundary falls in that bucket.
        histogram.Record(0.00390625, tags);
        histogram.Record(0.25, tags);
        histogram.Record(32, tags);

        var lines = Lines();
        Assert.Contains("# TYPE http_server_request_duration_seconds histogram", lines);
        const string labels = "http_request_method=\"GET\",http_route=\"/api/me\"";
        Assert.Contains($"http_server_request_duration_seconds_bucket{{{labels},le=\"0.005\"}} 1", lines);
        Assert.Contains($"http_server_request_duration_seconds_bucket{{{labels},le=\"0.1\"}} 1", lines);
        Assert.Contains($"http_server_request_duration_seconds_bucket{{{labels},le=\"0.25\"}} 2", lines);
        Assert.Contains($"http_server_request_duration_seconds_bucket{{{labels},le=\"10\"}} 2", lines);
        Assert.Contains($"http_server_request_duration_seconds_bucket{{{labels},le=\"+Inf\"}} 3", lines);
        Assert.Contains($"http_server_request_duration_seconds_sum{{{labels}}} 32.25390625", lines);
        Assert.Contains($"http_server_request_duration_seconds_count{{{labels}}} 3", lines);
    }

    [Fact]
    public void Request_metrics_keep_only_route_method_status_and_error_tags()
    {
        var histogram = Factory.Create("Microsoft.AspNetCore.Hosting").CreateHistogram<double>("http.server.request.duration", "s");

        histogram.Record(0.01,
            new KeyValuePair<string, object?>("http.route", "/api/rooms/{code}"),
            new KeyValuePair<string, object?>("http.response.status_code", 404),
            new KeyValuePair<string, object?>("url.path", "/api/rooms/ABC123"),
            new KeyValuePair<string, object?>("client.address", "203.0.113.9"));

        var text = _prometheus.Collect();
        Assert.Contains("http_server_request_duration_seconds_count{http_response_status_code=\"404\",http_route=\"/api/rooms/{code}\"} 1", text);
        Assert.DoesNotContain("ABC123", text);
        Assert.DoesNotContain("203.0.113.9", text);
    }

    [Fact]
    public void Up_down_counters_and_observable_gauges_are_gauges()
    {
        var meter = Factory.Create(ChogGamesMetrics.MeterName);
        var active = meter.CreateUpDownCounter<long>("choggames.test.active");
        var open = 4L;
        meter.CreateObservableGauge("choggames.test.open", () => open);

        active.Add(3);
        active.Add(-1);

        Assert.Contains("# TYPE choggames_test_active gauge", Lines());
        Assert.Contains("choggames_test_active 2", Lines());
        Assert.Contains("choggames_test_open 4", Lines());
        open = 1;
        Assert.Contains("choggames_test_open 1", Lines());
    }

    [Fact]
    public void Label_values_are_escaped_and_names_made_valid()
    {
        var counter = Factory.Create(ChogGamesMetrics.MeterName).CreateCounter<long>("choggames.test-escape");

        counter.Add(1, new KeyValuePair<string, object?>("odd.key", "a\"b\\c\nd"));

        Assert.Contains("choggames_test_escape_total{odd_key=\"a\\\"b\\\\c\\nd\"} 1", Lines());
    }

    [Fact]
    public void Other_meters_and_meters_of_other_hosts_are_ignored()
    {
        Factory.Create("Some.Library").CreateCounter<long>("library.calls").Add(1);
        using var otherHost = new ServiceCollection().AddMetrics().BuildServiceProvider();
        otherHost.GetRequiredService<IMeterFactory>().Create(ChogGamesMetrics.MeterName).CreateCounter<long>("choggames.rooms.created").Add(1);
        using var unscoped = new Meter(ChogGamesMetrics.MeterName);
        unscoped.CreateCounter<long>("choggames.rooms.created").Add(1);

        Assert.Equal("", _prometheus.Collect());
    }

    [Fact]
    public void Room_changes_count_each_transition_once_by_game_and_outcome()
    {
        var metrics = new ChogGamesMetrics(Factory);

        metrics.RoomChanged("chog-life", null, Core.Rooms.RoomStatus.Waiting);
        metrics.RoomChanged("chog-life", Core.Rooms.RoomStatus.Waiting, Core.Rooms.RoomStatus.Active);
        metrics.RoomChanged("chog-life", Core.Rooms.RoomStatus.Active, Core.Rooms.RoomStatus.Completed);
        metrics.RoomChanged("chog-life", Core.Rooms.RoomStatus.Active, Core.Rooms.RoomStatus.Abandoned);
        metrics.RoomChanged("chog-race", Core.Rooms.RoomStatus.Waiting, Core.Rooms.RoomStatus.Cancelled);

        var lines = Lines();
        Assert.Contains("choggames_rooms_created_total{game=\"chog-life\"} 1", lines);
        Assert.Contains("choggames_matches_started_total{game=\"chog-life\"} 1", lines);
        Assert.Contains("choggames_matches_ended_total{game=\"chog-life\",outcome=\"completed\"} 1", lines);
        Assert.Contains("choggames_matches_ended_total{game=\"chog-life\",outcome=\"abandoned\"} 1", lines);
        Assert.Contains("choggames_rooms_cancelled_total{game=\"chog-race\"} 1", lines);
    }
}
