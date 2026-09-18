using System.Net.Http.Json;
using Gauge.Ingest.Data;
using Gauge.Ingest.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gauge.Ingest.Tests;

/// <summary>
/// Görev B: Bu testleri geçirin. Skip'i kaldırın.
/// IngestBatchRequest'e bir BatchId alanı eklemeniz beklenir; testler ona göre yazıldı.
/// </summary>
public sealed class IdempotencyTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public IdempotencyTests()
    {
        _client = _app.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", "key-tenant-a");
    }

    private static object Batch(Guid batchId, int count = 20) => new
    {
        batchId,
        collectorId = "col-1",
        readings = Enumerable.Range(0, count).Select(i => new
        {
            meterId = $"MTR-A-{(i % 50) + 1:000}",
            timestamp = new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc).AddMinutes(15 * i),
            kwh = 1.0m + i
        }).ToArray()
    };

    private async Task<int> ReadingCountAsync()
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Readings.CountAsync();
    }

    private async Task<int> DirtyHourCountAsync()
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().DirtyHours.CountAsync();
    }

    [Fact]
    public async Task SameBatchTwice_WritesOnce()
    {
        var id = Guid.NewGuid();

        var first = await (await _client.PostAsJsonAsync("/api/readings", Batch(id))).Content.ReadFromJsonAsync<IngestResult>();
        var second = await (await _client.PostAsJsonAsync("/api/readings", Batch(id))).Content.ReadFromJsonAsync<IngestResult>();

        Assert.Equal(20, first!.Accepted);
        Assert.Equal(first.Accepted, second!.Accepted);
        Assert.Equal(20, await ReadingCountAsync());
    }

    [Fact]
    public async Task ConcurrentSameBatch_OnlyOneWrites()
    {
        var id = Guid.NewGuid();

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => _client.PostAsJsonAsync("/api/readings", Batch(id)))
            .ToArray();
        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"status {r.StatusCode}"));
        Assert.Equal(20, await ReadingCountAsync());
    }

    [Fact]
    public async Task DifferentBatches_BothWrite()
    {
        await _client.PostAsJsonAsync("/api/readings", Batch(Guid.NewGuid()));
        await _client.PostAsJsonAsync("/api/readings", Batch(Guid.NewGuid()));

        Assert.Equal(40, await ReadingCountAsync());
    }

    [Fact]
    public async Task DifferentBatchesForSameHour_CreateOneDirtyHour()
    {
        // Aynı meter/saate düşen iki farklı batch tek bir aggregation işi üretmeli.
        var first = await _client.PostAsJsonAsync("/api/readings", Batch(Guid.NewGuid(), count: 1));
        var second = await _client.PostAsJsonAsync("/api/readings", Batch(Guid.NewGuid(), count: 1));

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        Assert.Equal(1, await DirtyHourCountAsync());
    }

    public void Dispose() => _app.Dispose();
}
