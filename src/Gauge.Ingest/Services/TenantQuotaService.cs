using Gauge.Ingest.Data;
using Microsoft.EntityFrameworkCore;

namespace Gauge.Ingest.Services;

/// <summary>Tenant başına bu saat içinde kabul edilen okuma sayısını tutar (raporlama/faturalama için).</summary>
public sealed class TenantQuotaService(AppDbContext db)
{
    public Task<long> GetAsync(string tenantId, CancellationToken ct = default)
    {
        var hourStart = new DateTime(
            DateTime.UtcNow.Year,
            DateTime.UtcNow.Month,
            DateTime.UtcNow.Day,
            DateTime.UtcNow.Hour,
            0,
            0,
            DateTimeKind.Utc);
        var nextHour = hourStart.AddHours(1);

        // ProcessedBatch aynı transaction'da yazıldığı için kabul edilen veri kalıcıdır.
        return db.ProcessedBatches
            .Where(p => p.TenantId == tenantId &&
                        p.CreatedAt >= hourStart &&
                        p.CreatedAt < nextHour)
            .SumAsync(p => (long)p.Accepted, ct);
    }
}
