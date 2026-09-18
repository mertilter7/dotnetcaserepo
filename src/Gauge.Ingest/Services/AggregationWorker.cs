using Gauge.Ingest.Data;
using Gauge.Ingest.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gauge.Ingest.Services;

/// <summary>DirtyHours tablosunu tarayıp saatlik toplamları yeniden hesaplar.</summary>
public sealed class AggregationWorker : BackgroundService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AggregationWorker> _logger;

    public AggregationWorker(IServiceProvider services, ILogger<AggregationWorker> logger)
    {
        // Worker singleton olduğu için scope'u burada bir kere açıyoruz.
        var scope = services.CreateScope();
        _db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var dirty = await _db.DirtyHours.Take(500).ToListAsync();
            foreach (var d in dirty)
            {
                var total = await _db.Readings
                    .Where(r => r.MeterId == d.MeterId && r.Timestamp >= d.HourStart && r.Timestamp < d.HourStart.AddHours(1))
                    .SumAsync(r => r.Kwh);

                var agg = await _db.HourlyAggregates
                    .FirstOrDefaultAsync(h => h.MeterId == d.MeterId && h.HourStart == d.HourStart);

                if (agg is null)
                    _db.HourlyAggregates.Add(new HourlyAggregate { MeterId = d.MeterId, HourStart = d.HourStart, TotalKwh = total, ComputedAt = DateTime.UtcNow });
                else
                {
                    agg.TotalKwh = total;
                    agg.ComputedAt = DateTime.UtcNow;
                }
                _db.DirtyHours.Remove(d);
            }

            if (dirty.Count > 0)
            {
                await _db.SaveChangesAsync();
                _logger.LogInformation("Recomputed {Count} hours", dirty.Count);
            }

            Thread.Sleep(TimeSpan.FromSeconds(5));
        }
    }
}
