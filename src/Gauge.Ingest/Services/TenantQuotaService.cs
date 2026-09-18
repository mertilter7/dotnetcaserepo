namespace Gauge.Ingest.Services;

/// <summary>Tenant başına bu saat içinde kabul edilen okuma sayısını tutar (raporlama/faturalama için).</summary>
public sealed class TenantQuotaService
{
    private static readonly Dictionary<string, long> Counters = new();

    public void Record(string tenantId, int count)
    {
        if (Counters.ContainsKey(tenantId))
            Counters[tenantId] += count;
        else
            Counters[tenantId] = count;
    }

    public long Get(string tenantId) => Counters.TryGetValue(tenantId, out var v) ? v : 0;
}
