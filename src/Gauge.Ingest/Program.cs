using Gauge.Ingest.Data;
using Gauge.Ingest.Endpoints;
using Gauge.Ingest.Services;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var tenantConcurrencyLimit = Math.Max(
    1,
    builder.Configuration.GetValue("Ingest:TenantConcurrencyLimit", 4));
var tenantQueueLimit = Math.Max(
    0,
    builder.Configuration.GetValue("Ingest:TenantQueueLimit", 8));
var retryAfterSeconds = Math.Max(
    1,
    builder.Configuration.GetValue("Ingest:RetryAfterSeconds", 5));

builder.Services.AddDbContext<SqliteAppDbContext>((serviceProvider, o) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    o.UseSqlite(configuration.GetConnectionString("Default") ?? "Data Source=gauge.db");
});
builder.Services.AddDbContext<SqlServerAppDbContext>((serviceProvider, o) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var connection = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("DefaultConnection is required for SQL Server.");
    o.UseSqlServer(connection);
});
builder.Services.AddScoped<AppDbContext>(serviceProvider =>
{
    // Testler boş DefaultConnection ile SQLite context'ini, production SQL Server context'ini kullanır.
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    return string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection"))
        ? serviceProvider.GetRequiredService<SqliteAppDbContext>()
        : serviceProvider.GetRequiredService<SqlServerAppDbContext>();
});
builder.Services.AddScoped<ReadingIngestService>();
// Quota database context kullandığı için request scope ile aynı yaşam döngüsünde olmalı.
builder.Services.AddScoped<TenantQuotaService>();
builder.Services.AddSingleton<GaugeMetrics>();
builder.Services.AddHostedService<AggregationWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        // Collector, 429 sonrası ne zaman tekrar deneyeceğini bu header'dan öğrenir.
        context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        context.HttpContext.RequestServices
            .GetRequiredService<GaugeMetrics>()
            .RateLimitRejections.Add(1);
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
    // SQLite test/local schema'sı izole oluşturulur; production SQL Server migration kullanır.
    if (db.Database.IsSqlite())
        db.Database.EnsureCreated();
    else
        db.Database.Migrate();
    Seed.Run(db);
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseRateLimiter();
ReadingsEndpoint.Map(app);
app.Run();

public partial class Program { }
