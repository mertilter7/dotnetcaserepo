using System.Net.Http.Json;
using Gauge.Ingest.Data;
using Gauge.Ingest.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gauge.Ingest.Tests;

public sealed class AggregationTests : IDisposable
{
    private readonly TestApp _app = new(runAggregationWorker: true);
    private readonly HttpClient _client;

    public AggregationTests()
    {
        _client = _app.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", "key-tenant-a");
    }

    [Fact]
    public async Task LateReading_RecomputesTheAffectedHour()
    {
        var hour = new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc);

        await SendAsync(Guid.NewGuid(), hour.AddMinutes(15), 1.5m);
        var firstTotal = await WaitForTotalAsync("MTR-A-001", hour);
        Assert.Equal(1.5m, firstTotal);

        // A late reading for the same hour must trigger a full recomputation.
        await SendAsync(Guid.NewGuid(), hour.AddMinutes(30), 2.5m);
        var finalTotal = await WaitForTotalAsync("MTR-A-001", hour, expected: 4m);

        Assert.Equal(4m, finalTotal);
    }

    private async Task SendAsync(Guid batchId, DateTime timestamp, decimal kwh)
    {
        var request = new IngestBatchRequest(
            "col-1",
            [new("MTR-A-001", timestamp, kwh)],
            batchId);

        var response = await _client.PostAsJsonAsync("/api/readings", request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<decimal> WaitForTotalAsync(
        string meterId,
        DateTime hour,
        decimal? expected = null)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var aggregate = await db.HourlyAggregates
                .SingleOrDefaultAsync(h => h.MeterId == meterId && h.HourStart == hour);

            if (aggregate is not null && (expected is null || aggregate.TotalKwh == expected))
                return aggregate.TotalKwh;

            await Task.Delay(100);
        }

        throw new TimeoutException($"Aggregate was not updated for {meterId} at {hour:u}.");
    }

    public void Dispose() => _app.Dispose();
}
