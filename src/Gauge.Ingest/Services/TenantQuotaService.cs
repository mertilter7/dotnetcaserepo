using System.Collections.Concurrent;

namespace Gauge.Ingest.Services;

/// <summary>Tenant başına bu saat içinde kabul edilen okuma sayısını tutar (raporlama/faturalama için).</summary>
public sealed class TenantQuotaService
{
    // Ingest requests run concurrently across threads and API instances.
    private static readonly ConcurrentDictionary<string, long> Counters = new();

    public void Record(string tenantId, int count)
    {
        // Atomic update prevents concurrent requests from corrupting the counter.
        Counters.AddOrUpdate(tenantId, count, (_, current) => current + count);
    }

    public long Get(string tenantId) => Counters.TryGetValue(tenantId, out var v) ? v : 0;
}
