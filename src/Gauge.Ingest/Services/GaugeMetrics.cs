using System.Diagnostics.Metrics;

namespace Gauge.Ingest.Services;

public sealed class GaugeMetrics : IDisposable
{
    private readonly Meter _meter = new("Gauge.Ingest");

    public Counter<long> IngestBatches { get; }
    public Counter<long> AcceptedReadings { get; }
    public Counter<long> RejectedReadings { get; }
    public Counter<long> DuplicateBatches { get; }
    public Counter<long> RateLimitRejections { get; }
    public Counter<long> WorkerClaims { get; }
    public Counter<long> AggregatedHours { get; }
    public Histogram<double> IngestDurationMs { get; }
    public Histogram<double> AggregationDurationMs { get; }

    public GaugeMetrics()
    {
        IngestBatches = _meter.CreateCounter<long>("gauge.ingest.batches");
        AcceptedReadings = _meter.CreateCounter<long>("gauge.ingest.readings.accepted");
        RejectedReadings = _meter.CreateCounter<long>("gauge.ingest.readings.rejected");
        DuplicateBatches = _meter.CreateCounter<long>("gauge.ingest.batches.duplicate");
        RateLimitRejections = _meter.CreateCounter<long>("gauge.ingest.rate_limit.rejected");
        WorkerClaims = _meter.CreateCounter<long>("gauge.aggregation.claims");
        AggregatedHours = _meter.CreateCounter<long>("gauge.aggregation.hours");
        IngestDurationMs = _meter.CreateHistogram<double>("gauge.ingest.duration_ms");
        AggregationDurationMs = _meter.CreateHistogram<double>("gauge.aggregation.duration_ms");
    }

    public static KeyValuePair<string, object?> TenantTag(string tenantId) =>
        new("tenant_id", tenantId);

    public void Dispose() => _meter.Dispose();
}
