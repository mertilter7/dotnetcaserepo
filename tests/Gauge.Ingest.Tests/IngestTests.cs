using System.Net;
using System.Net.Http.Json;
using Gauge.Ingest.Services;

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

    public void Dispose() => _app.Dispose();
}
