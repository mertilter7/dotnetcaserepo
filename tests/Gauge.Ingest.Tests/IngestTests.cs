using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gauge.Ingest.Data;
using Gauge.Ingest.Domain;
using Gauge.Ingest.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Gauge.Ingest.Tests;

public sealed class IngestTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public IngestTests()
    {
        _client = _app.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", "key-tenant-a");
    }

    [Fact]
    public async Task ValidBatch_IsAccepted()
    {
        var req = new IngestBatchRequest("col-1", [new("MTR-A-001", new DateTime(2026, 9, 18, 10, 15, 0, DateTimeKind.Utc), 1.5m)]);
        var res = await _client.PostAsJsonAsync("/api/readings", req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<IngestResult>();
        Assert.Equal(1, body!.Accepted);
        Assert.Equal(0, body.Rejected);
    }

    [Fact]
    public async Task MeterOfOtherTenant_IsRejected()
    {
        var req = new IngestBatchRequest("col-1", [new("MTR-B-001", DateTime.UtcNow, 1m)]);
        var body = await (await _client.PostAsJsonAsync("/api/readings", req)).Content.ReadFromJsonAsync<IngestResult>();

        Assert.Equal(0, body!.Accepted);
        Assert.Equal(1, body.Rejected);
    }

    [Fact]
    public async Task MissingApiKey_IsUnauthorized()
    {
        using var anon = _app.CreateClient();
        var res = await anon.PostAsJsonAsync("/api/readings", new IngestBatchRequest("c", [new("MTR-A-001", DateTime.UtcNow, 1m)]));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task HourlyReport_DoesNotReturnAnotherTenantsData()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.HourlyAggregates.Add(new HourlyAggregate
            {
                MeterId = "MTR-B-001",
                HourStart = new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc),
                TotalKwh = 12m,
                ComputedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var res = await _client.GetAsync("/api/meters/MTR-B-001/hourly");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<List<HourlyAggregate>>();
        Assert.Empty(body!);
    }

    [Fact]
    public async Task UsageReport_CannotReadAnotherTenant()
    {
        var res = await _client.GetAsync("/api/tenants/tenant-b/usage");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task UsageReport_ReadsCommittedBatchesForCurrentHour()
    {
        var request = new IngestBatchRequest(
            "col-1",
            [
                new("MTR-A-001", DateTime.UtcNow, 1m),
                new("MTR-A-002", DateTime.UtcNow, 1m)
            ],
            Guid.NewGuid());

        var ingest = await _client.PostAsJsonAsync("/api/readings", request);
        ingest.EnsureSuccessStatusCode();

        var usage = await _client.GetFromJsonAsync<JsonElement>("/api/tenants/tenant-a/usage");

        Assert.Equal(2, usage.GetProperty("readingsThisHour").GetInt64());
    }

    public void Dispose() => _app.Dispose();
}
