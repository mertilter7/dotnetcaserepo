using Gauge.Ingest.Data;
using Gauge.Ingest.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Gauge.Ingest.Services;

public sealed class ReadingIngestService(
    AppDbContext db,
    TenantQuotaService quota,
    ILogger<ReadingIngestService> logger)
{
    public async Task<IngestResult> IngestAsync(
        string tenantId,
        IngestBatchRequest request,
        CancellationToken ct = default)
    {
        // Existing callers without BatchId remain compatible; new callers get idempotency.
        var batchId = request.BatchId == Guid.Empty ? Guid.NewGuid() : request.BatchId;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            // Fast path for retries; the unique index below is still the concurrency guarantee.
            var processed = await db.ProcessedBatches
                .AsNoTracking()
                .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.BatchId == batchId, ct);

            if (processed is not null)
                return ToResult(processed);

            var errors = new List<string>();
            var accepted = 0;
            var now = DateTime.UtcNow;
            var meterIds = request.Readings.Select(r => r.MeterId).Distinct().ToList();
            var meters = await db.Meters
                .Where(m => m.TenantId == tenantId && meterIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, ct);

            // One DbContext is used sequentially; EF DbContext is not thread-safe.
            foreach (var dto in request.Readings)
            {
                if (!meters.ContainsKey(dto.MeterId))
                {
                    errors.Add($"{dto.MeterId}: unknown meter for tenant");
                    continue;
                }
                if (dto.Kwh < 0)
                {
                    errors.Add($"{dto.MeterId}: negative kwh");
                    continue;
                }

                db.Readings.Add(new Reading
                {
                    MeterId = dto.MeterId,
                    Timestamp = dto.Timestamp,
                    Kwh = dto.Kwh,
                    ReceivedAt = now
                });
                accepted++;
            }

            var result = new IngestResult(accepted, errors.Count, errors);
            db.ProcessedBatches.Add(new ProcessedBatch
            {
                TenantId = tenantId,
                BatchId = batchId,
                Accepted = result.Accepted,
                Rejected = result.Rejected,
                ErrorsJson = JsonSerializer.Serialize(result.Errors),
                CreatedAt = now
            });

            // Transaction: readings, batch result, and aggregation triggers commit together.
            var hours = request.Readings
                .Where(r => meters.ContainsKey(r.MeterId) && r.Kwh >= 0)
                .Select(r => (
                    r.MeterId,
                    Hour: new DateTime(r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, r.Timestamp.Hour, 0, 0, DateTimeKind.Utc)))
                .Distinct()
                .ToList();

            foreach (var (meterId, hour) in hours)
                db.DirtyHours.Add(new DirtyHour { MeterId = meterId, HourStart = hour });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            quota.Record(tenantId, accepted);

            logger.LogInformation(
                "Ingested batch {BatchId}: {Accepted} readings for {Tenant} from {Collector}",
                batchId, accepted, tenantId, request.CollectorId);
            return result;
        }
        catch (DbUpdateException)
        {
            // Concurrent duplicate: the unique index rejected this transaction.
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            var existing = await db.ProcessedBatches
                .AsNoTracking()
                .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.BatchId == batchId, ct);

            if (existing is not null)
                return ToResult(existing);

            throw;
        }
    }

    private static IngestResult ToResult(ProcessedBatch batch)
    {
        var errors = JsonSerializer.Deserialize<List<string>>(batch.ErrorsJson) ?? [];
        return new IngestResult(batch.Accepted, batch.Rejected, errors);
    }
}
