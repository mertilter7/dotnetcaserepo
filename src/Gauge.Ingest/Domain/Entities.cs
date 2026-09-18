namespace Gauge.Ingest.Domain;

public sealed class Tenant
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string ApiKey { get; set; } = default!;
}

public sealed class Meter
{
    public string Id { get; set; } = default!;
    public string TenantId { get; set; } = default!;
}

public sealed class Reading
{
    public long Id { get; set; }
    public string MeterId { get; set; } = default!;
    public DateTime Timestamp { get; set; }
    public decimal Kwh { get; set; }
    public DateTime ReceivedAt { get; set; }
}

public sealed class HourlyAggregate
{
    public long Id { get; set; }
    public string MeterId { get; set; } = default!;
    public DateTime HourStart { get; set; }
    public decimal TotalKwh { get; set; }
    public DateTime ComputedAt { get; set; }
}

/// <summary>Worker'ın yeniden hesaplaması gereken (sayaç, saat) çiftleri.</summary>
public sealed class DirtyHour
{
    public long Id { get; set; }
    public string MeterId { get; set; } = default!;
    public DateTime HourStart { get; set; }
    public string? LeaseId { get; set; }
    public DateTime? LeaseUntil { get; set; }
}

public sealed class ProcessedBatch
{
    public long Id { get; set; }
    public string TenantId { get; set; } = default!;
    public Guid BatchId { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public string ErrorsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
}
