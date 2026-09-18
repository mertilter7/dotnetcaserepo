using Gauge.Ingest.Data;
using Gauge.Ingest.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gauge.Ingest.Services;

/// <summary>DirtyHours tablosunu tarayıp saatlik toplamları yeniden hesaplar.</summary>
public sealed class AggregationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AggregationWorker> _logger;

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
                var dirty = await db.DirtyHours.Take(500).ToListAsync(stoppingToken);

                foreach (var d in dirty)
                {
                    var total = await db.Readings
                        .Where(r => r.MeterId == d.MeterId &&
                                    r.Timestamp >= d.HourStart &&
                                    r.Timestamp < d.HourStart.AddHours(1))
                        .SumAsync(r => r.Kwh, stoppingToken);

                    var agg = await db.HourlyAggregates
                        .FirstOrDefaultAsync(
                            h => h.MeterId == d.MeterId && h.HourStart == d.HourStart,
                            stoppingToken);

                    if (agg is null)
                    {
                        db.HourlyAggregates.Add(new HourlyAggregate
                        {
                            MeterId = d.MeterId,
                            HourStart = d.HourStart,
                            TotalKwh = total,
                            ComputedAt = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        agg.TotalKwh = total;
                        agg.ComputedAt = DateTime.UtcNow;
                    }

                    db.DirtyHours.Remove(d);
                }

                if (dirty.Count > 0)
                {
                    await db.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation("Recomputed {Count} hours", dirty.Count);
                }

                // Do not block a thread; stop promptly when the host is shutting down.
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
