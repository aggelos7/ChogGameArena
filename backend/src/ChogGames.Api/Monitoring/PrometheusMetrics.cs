using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using ChogGames.Infrastructure.Monitoring;

namespace ChogGames.Api.Monitoring;

/// <summary>
/// Collects this host's metrics and writes them in the Prometheus text format (version 0.0.4) for <c>GET /metrics</c>
/// (STORY-048, DEC-052), with no exporter dependency. It listens to the game and session meter and to ASP.NET Core's
/// HTTP and SignalR meters, and only to meters made by this host's <see cref="IMeterFactory"/>, so hosts in one process
/// never mix. Values are kept since the start: counters and histograms are cumulative, as Prometheus expects.
/// </summary>
public sealed class PrometheusMetrics : IDisposable
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    private const string HostingMeter = "Microsoft.AspNetCore.Hosting";
    private const string ConnectionsMeter = "Microsoft.AspNetCore.Http.Connections";

    // Only these tags are kept, so a series is never per player, room, path or client. null keeps every tag.
    private static readonly Dictionary<string, HashSet<string>?> Meters = new(StringComparer.Ordinal)
    {
        [ChogGamesMetrics.MeterName] = null,
        [HostingMeter] = ["http.route", "http.request.method", "http.response.status_code", "error.type"],
        [ConnectionsMeter] = ["signalr.connection.status", "signalr.transport"],
    };

    // Request durations, in seconds: ASP.NET Core's recommended boundaries.
    private static readonly double[] DefaultBuckets = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];

    // Connections last up to hours.
    private static readonly Dictionary<string, double[]> Buckets = new(StringComparer.Ordinal)
    {
        ["signalr.server.connection.duration"] = [1, 10, 60, 300, 900, 1800, 3600, 7200, 14400],
    };

    private readonly IMeterFactory _meterFactory;
    private readonly MeterListener _listener;
    private readonly ConcurrentDictionary<Instrument, Exported> _instruments = new();

    public PrometheusMetrics(IMeterFactory meterFactory)
    {
        _meterFactory = meterFactory;
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Scope == _meterFactory && Meters.TryGetValue(instrument.Meter.Name, out var tags))
                {
                    var exported = new Exported(instrument, tags);
                    _instruments[instrument] = exported;
                    listener.EnableMeasurementEvents(instrument, exported);
                }
            },
            MeasurementsCompleted = (instrument, _) => _instruments.TryRemove(instrument, out Exported? _),
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, state) => ((Exported)state!).Record(value, tags));
        _listener.SetMeasurementEventCallback<int>((_, value, tags, state) => ((Exported)state!).Record(value, tags));
        _listener.SetMeasurementEventCallback<double>((_, value, tags, state) => ((Exported)state!).Record(value, tags));
        _listener.Start();
    }

    /// <summary>Reads the observable instruments now, then writes every series.</summary>
    public string Collect()
    {
        _listener.RecordObservableInstruments();

        var text = new StringBuilder();
        foreach (var exported in _instruments.Values.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            exported.WriteTo(text);
        }

        return text.ToString();
    }

    public void Dispose() => _listener.Dispose();

    private enum Kind
    {
        Counter,
        Gauge,
        Histogram,
    }

    private sealed class Series(int buckets)
    {
        public double Value;
        public long Count;
        public readonly long[] BucketCounts = new long[buckets];
    }

    private sealed class Exported
    {
        private readonly Kind _kind;
        private readonly bool _observable;
        private readonly HashSet<string>? _tags;
        private readonly double[] _bounds;
        private readonly string? _help;
        private readonly ConcurrentDictionary<string, Series> _series = new(StringComparer.Ordinal);

        public Exported(Instrument instrument, HashSet<string>? tags)
        {
            _tags = tags;
            _help = instrument.Description;
            _observable = instrument.IsObservable;
            (_kind, var suffix) = instrument switch
            {
                Counter<long> or Counter<int> or Counter<double> or ObservableCounter<long> or ObservableCounter<int> or ObservableCounter<double>
                    => (Kind.Counter, "_total"),
                Histogram<long> or Histogram<int> or Histogram<double> => (Kind.Histogram, ""),
                _ => (Kind.Gauge, ""),
            };
            _bounds = _kind == Kind.Histogram ? Buckets.GetValueOrDefault(instrument.Name, DefaultBuckets) : [];
            Name = MetricName(instrument.Name) + (instrument.Unit == "s" ? "_seconds" : "") + suffix;
        }

        public string Name { get; }

        public void Record(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var series = _series.GetOrAdd(Labels(tags), _ => new Series(_bounds.Length));
            lock (series)
            {
                switch (_kind)
                {
                    case Kind.Histogram:
                        series.Count++;
                        series.Value += value;
                        for (var i = 0; i < _bounds.Length; i++)
                        {
                            if (value <= _bounds[i])
                            {
                                series.BucketCounts[i]++;
                            }
                        }

                        break;
                    case var _ when _observable:
                        // An observable reports its current (or cumulative) value on every read.
                        series.Value = value;
                        break;
                    default:
                        series.Value += value;
                        break;
                }
            }
        }

        public void WriteTo(StringBuilder text)
        {
            if (_series.IsEmpty)
            {
                return;
            }

            if (!string.IsNullOrEmpty(_help))
            {
                text.Append("# HELP ").Append(Name).Append(' ').Append(_help.Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n');
            }

            text.Append("# TYPE ").Append(Name).Append(' ').Append(_kind.ToString().ToLowerInvariant()).Append('\n');
            foreach (var (labels, series) in _series.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                lock (series)
                {
                    if (_kind != Kind.Histogram)
                    {
                        Line(text, Name, labels, null, series.Value);
                        continue;
                    }

                    for (var i = 0; i < _bounds.Length; i++)
                    {
                        Line(text, Name + "_bucket", labels, Number(_bounds[i]), series.BucketCounts[i]);
                    }

                    Line(text, Name + "_bucket", labels, "+Inf", series.Count);
                    Line(text, Name + "_sum", labels, null, series.Value);
                    Line(text, Name + "_count", labels, null, series.Count);
                }
            }
        }

        private string Labels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var kept = new List<KeyValuePair<string, string>>(tags.Length);
            foreach (var tag in tags)
            {
                if (_tags is null || _tags.Contains(tag.Key))
                {
                    kept.Add(new(MetricName(tag.Key), Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? ""));
                }
            }

            kept.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return string.Join(",", kept.Select(tag => $"{tag.Key}=\"{Escape(tag.Value)}\""));
        }

        private static void Line(StringBuilder text, string name, string labels, string? le, double value)
        {
            text.Append(name);
            if (labels.Length > 0 || le is not null)
            {
                text.Append('{').Append(labels);
                if (le is not null)
                {
                    text.Append(labels.Length > 0 ? "," : "").Append("le=\"").Append(le).Append('"');
                }

                text.Append('}');
            }

            text.Append(' ').Append(Number(value)).Append('\n');
        }
    }

    /// <summary>A Prometheus metric or label name: <c>http.server.request.duration</c> becomes <c>http_server_request_duration</c>.</summary>
    internal static string MetricName(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(chars[i]) || chars[i] == '_') || (i == 0 && char.IsAsciiDigit(chars[i])))
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    internal static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static string Number(double value) => value switch
    {
        double.PositiveInfinity => "+Inf",
        double.NegativeInfinity => "-Inf",
        _ => value.ToString("R", CultureInfo.InvariantCulture),
    };
}
