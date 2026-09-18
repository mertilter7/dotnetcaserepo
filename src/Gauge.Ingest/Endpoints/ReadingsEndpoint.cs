using Gauge.Ingest.Data;
using Gauge.Ingest.Services;
using Microsoft.EntityFrameworkCore;

namespace Gauge.Ingest.Endpoints;

public static class ReadingsEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/readings", async (
            HttpContext http,
            AppDbContext db,
            ReadingIngestService ingest,
            IngestBatchRequest request,
            CancellationToken ct) =>
        {
            var apiKey = http.Request.Headers["X-Api-Key"].ToString();
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.ApiKey == apiKey, ct);
            if (tenant is null) return Results.Unauthorized();

            if (request.Readings.Count == 0) return Results.BadRequest("empty batch");

            var result = await ingest.IngestAsync(tenant.Id, request, ct);
            return Results.Ok(result);
        })
        .WithName("IngestReadings")
        .WithOpenApi();

        app.MapGet("/api/meters/{meterId}/hourly", async (string meterId, AppDbContext db) =>
            Results.Ok(await db.HourlyAggregates
                .Where(h => h.MeterId == meterId)
                .OrderBy(h => h.HourStart)
                .ToListAsync()));

        app.MapGet("/api/tenants/{tenantId}/usage", (string tenantId, TenantQuotaService quota) =>
            Results.Ok(new { tenantId, readingsThisHour = quota.Get(tenantId) }));
    }
}
