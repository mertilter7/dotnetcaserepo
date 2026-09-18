using Gauge.Ingest.Data;
using Gauge.Ingest.Endpoints;
using Gauge.Ingest.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var sqlServerConnection = builder.Configuration.GetConnectionString("SqlServer");

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
ReadingsEndpoint.Map(app);
app.Run();

public partial class Program { }
