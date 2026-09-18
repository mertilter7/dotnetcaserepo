using Gauge.Ingest.Data;
using Gauge.Ingest.Domain;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;

namespace Gauge.Ingest.Services;

public sealed class ReadingIngestService(
    AppDbContext db,
    ILogger<ReadingIngestService> logger,
    GaugeMetrics metrics)
{
    public async Task<IngestResult> IngestAsync(
        string tenantId,
        IngestBatchRequest request,
        CancellationToken ct = default)
    {
        // A different batch may race on the same unique DirtyHour key; retry the whole transaction.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await IngestCoreAsync(tenantId, request, ct);
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), ct);
            }
        }
    }

    private async Task<IngestResult> IngestCoreAsync(
        string tenantId,
        IngestBatchRequest request,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // Endpoint batchId'yi zorunlu tuttuğu için burada fallback GUID üretmiyoruz.
        var batchId = request.BatchId;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            // Fast path for retries; the unique index below is still the concurrency guarantee.
            var processed = await db.ProcessedBatches
                .AsNoTracking()
                .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.BatchId == batchId, ct);

            if (processed is not null)
            {
                // Duplicate batch yeni veri yazmaz; bu tekrarları ayrıca ölçüyoruz.
                metrics.DuplicateBatches.Add(1, GaugeMetrics.TenantTag(tenantId));
                metrics.IngestDurationMs.Record(
                    stopwatch.Elapsed.TotalMilliseconds,
                    GaugeMetrics.TenantTag(tenantId));
                return ToResult(processed);
            }

            var errors = new List<string>();
            var readingsToInsert = new List<Reading>(request.Readings.Count);
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

                readingsToInsert.Add(new Reading
                {
                    MeterId = dto.MeterId,
                    Timestamp = dto.Timestamp,
                    Kwh = dto.Kwh,
                    ReceivedAt = now
                });
            }

            // Batch insert: keep one transaction while avoiding one Add call per entity.
            db.Readings.AddRange(readingsToInsert);
            var accepted = readingsToInsert.Count;
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

            // Avoid adding the same pending aggregation key more than once.
            var dirtyMeterIds = hours.Select(h => h.MeterId).Distinct().ToList();
            var existingDirty = await db.DirtyHours
                .Where(d => dirtyMeterIds.Contains(d.MeterId))
                .ToListAsync(ct);
            var existingDirtyKeys = existingDirty
                .Select(d => (d.MeterId, d.HourStart))
                .ToHashSet();

            foreach (var (meterId, hour) in hours)
            {
                if (existingDirtyKeys.Add((meterId, hour)))
                    db.DirtyHours.Add(new DirtyHour { MeterId = meterId, HourStart = hour });
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            // Metric yalnızca transaction commit edildikten sonra kaydedilir.
            metrics.IngestBatches.Add(1, GaugeMetrics.TenantTag(tenantId));
            metrics.AcceptedReadings.Add(accepted, GaugeMetrics.TenantTag(tenantId));
            metrics.RejectedReadings.Add(errors.Count, GaugeMetrics.TenantTag(tenantId));
            metrics.IngestDurationMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                GaugeMetrics.TenantTag(tenantId));

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
            {
                // Unique constraint ile yakalanan concurrent retry de duplicate metriğine dahil edilir.
                metrics.DuplicateBatches.Add(1, GaugeMetrics.TenantTag(tenantId));
                metrics.IngestDurationMs.Record(
                    stopwatch.Elapsed.TotalMilliseconds,
                    GaugeMetrics.TenantTag(tenantId));
                return ToResult(existing);
            }

            throw;
        }
    }

    private static IngestResult ToResult(ProcessedBatch batch)
    {
        var errors = JsonSerializer.Deserialize<List<string>>(batch.ErrorsJson) ?? [];
        return new IngestResult(batch.Accepted, batch.Rejected, errors);
    }
}
