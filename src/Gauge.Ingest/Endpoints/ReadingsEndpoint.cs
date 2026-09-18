using Gauge.Ingest.Data;
using Gauge.Ingest.Domain;
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
        // Backpressure tenant bazlıdır; reporting endpoint'leri bu limitle etkilenmez.
        .RequireRateLimiting("tenant-ingest")
        .WithOpenApi();

        app.MapGet("/api/meters/{meterId}/hourly", async (
            string meterId,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var tenant = await ResolveTenantAsync(http, db, ct);
            if (tenant is null) return Results.Unauthorized();

            // Tenant isolation: only aggregates belonging to the API key's tenant are visible.
            var aggregates = await db.Meters
                .Where(m => m.Id == meterId && m.TenantId == tenant.Id)
                .SelectMany(m => db.HourlyAggregates
                    .Where(h => h.MeterId == m.Id)
                    .OrderBy(h => h.HourStart))
                .ToListAsync(ct);

            return Results.Ok(aggregates);
        });

        app.MapGet("/api/tenants/{tenantId}/usage", async (
            string tenantId,
            HttpContext http,
            AppDbContext db,
            TenantQuotaService quota,
            CancellationToken ct) =>
        {
            var tenant = await ResolveTenantAsync(http, db, ct);
            if (tenant is null) return Results.Unauthorized();

            // The URL tenantId is not trusted; the authenticated tenant is the authority.
            if (tenant.Id != tenantId)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            // Usage commit edilmiş batch kayıtlarından okunduğu için iki VM'de tutarlıdır.
            var readingsThisHour = await quota.GetAsync(tenant.Id, ct);
            return Results.Ok(new { tenantId = tenant.Id, readingsThisHour });
        });
    }

    private static Task<Tenant?> ResolveTenantAsync(
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        // Tenant scope is derived from the API key, never from a user-controlled route value.
        var apiKey = http.Request.Headers["X-Api-Key"].ToString();
        return db.Tenants.FirstOrDefaultAsync(t => t.ApiKey == apiKey, ct);
    }
}
