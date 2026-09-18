using Gauge.Ingest.Data;
using Gauge.Ingest.Endpoints;
using Gauge.Ingest.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=gauge.db"));
builder.Services.AddScoped<ReadingIngestService>();
// Quota sorgusu scoped DbContext kullandığı için service singleton olamaz.
builder.Services.AddScoped<TenantQuotaService>();
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
