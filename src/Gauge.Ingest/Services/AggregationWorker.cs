using Gauge.Ingest.Data;
using Gauge.Ingest.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gauge.Ingest.Services;

/// <summary>DirtyHours tablosunu tarayıp saatlik toplamları yeniden hesaplar.</summary>
public sealed class AggregationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AggregationWorker> _logger;
    private readonly string _workerId = Guid.NewGuid().ToString("N");
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    public AggregationWorker(IServiceScopeFactory scopeFactory, ILogger<AggregationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A hosted service is singleton; use a fresh scoped DbContext per iteration.
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var processed = 0;

                for (var i = 0; i < 500; i++)
                {
                    var dirty = await ClaimNextAsync(db, stoppingToken);
                    if (dirty is null)
                        break;

                    // Geç gelen reading için saati yeniden topluyoruz; sadece yeni değeri eklemiyoruz.
                    var total = await db.Readings
                        .Where(r => r.MeterId == dirty.MeterId &&
                                    r.Timestamp >= dirty.HourStart &&
                                    r.Timestamp < dirty.HourStart.AddHours(1))
                        .SumAsync(r => r.Kwh, stoppingToken);

                    await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                    var agg = await db.HourlyAggregates
                        .FirstOrDefaultAsync(
                            h => h.MeterId == dirty.MeterId && h.HourStart == dirty.HourStart,
                            stoppingToken);

                    if (agg is null)
                    {
                        db.HourlyAggregates.Add(new HourlyAggregate
                        {
                            MeterId = dirty.MeterId,
                            HourStart = dirty.HourStart,
                            TotalKwh = total,
                            ComputedAt = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        agg.TotalKwh = total;
                        agg.ComputedAt = DateTime.UtcNow;
                    }

                    await db.SaveChangesAsync(stoppingToken);
                    // Lease sahibi değilsek başka worker kaydın lease'ini almıştır; silme.
                    await db.DirtyHours
                        .Where(d => d.Id == dirty.Id && d.LeaseId == _workerId)
                        .ExecuteDeleteAsync(stoppingToken);
                    await transaction.CommitAsync(stoppingToken);
                    processed++;
                }

                if (processed > 0)
                    _logger.LogInformation("Recomputed {Count} hours", processed);

                // Do not block a thread; stop promptly when the host is shutting down.
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<DirtyHour?> ClaimNextAsync(
        AppDbContext db,
        CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;
        var candidate = await db.DirtyHours
            .AsNoTracking()
            .Where(d => d.LeaseUntil == null || d.LeaseUntil <= now)
            .OrderBy(d => d.Id)
            .FirstOrDefaultAsync(stoppingToken);

        if (candidate is null)
            return null;

        var leaseUntil = now.Add(LeaseDuration);
        var claimed = await db.DirtyHours
            .Where(d => d.Id == candidate.Id &&
                        (d.LeaseUntil == null || d.LeaseUntil <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(d => d.LeaseId, _workerId)
                .SetProperty(d => d.LeaseUntil, leaseUntil), stoppingToken);

        if (claimed == 0)
            return null;

        // Atomik update başarılıysa bu worker artık kaydın sahibidir.
        return await db.DirtyHours
            .AsNoTracking()
            .SingleAsync(d => d.Id == candidate.Id, stoppingToken);
    }
}
