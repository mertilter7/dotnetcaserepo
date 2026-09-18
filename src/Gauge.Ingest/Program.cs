using Gauge.Ingest.Data;
using Gauge.Ingest.Endpoints;
using Gauge.Ingest.Services;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var sqlServerConnection = builder.Configuration.GetConnectionString("SqlServer");
var tenantConcurrencyLimit = Math.Max(
    1,
    builder.Configuration.GetValue("Ingest:TenantConcurrencyLimit", 4));
var tenantQueueLimit = Math.Max(
    0,
    builder.Configuration.GetValue("Ingest:TenantQueueLimit", 8));
var retryAfterSeconds = Math.Max(
    1,
    builder.Configuration.GetValue("Ingest:RetryAfterSeconds", 5));

builder.Services.AddDbContext<AppDbContext>(o =>
{
    // Production can use SQL Server; local tests keep the SQLite fallback.
    if (!string.IsNullOrWhiteSpace(sqlServerConnection))
        o.UseSqlServer(sqlServerConnection);
    else
        o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=gauge.db");
});
builder.Services.AddScoped<ReadingIngestService>();
builder.Services.AddSingleton<TenantQuotaService>();
builder.Services.AddHostedService<AggregationWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        // Collector, 429 sonrası ne zaman tekrar deneyeceğini bu header'dan öğrenir.
        context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        return ValueTask.CompletedTask;
    };

    // API key partition'ı, tek tenant'ın tüm ingest kaynaklarını tüketmesini sınırlar.
    options.AddPolicy("tenant-ingest", httpContext =>
        RateLimitPartition.GetConcurrencyLimiter(
            httpContext.Request.Headers["X-Api-Key"].ToString(),
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = tenantConcurrencyLimit,
                QueueLimit = tenantQueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    Seed.Run(db);
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseRateLimiter();
ReadingsEndpoint.Map(app);
app.Run();

public partial class Program { }
