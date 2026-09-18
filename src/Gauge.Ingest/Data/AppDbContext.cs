using Gauge.Ingest.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gauge.Ingest.Data;

public class AppDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Meter> Meters => Set<Meter>();
    public DbSet<Reading> Readings => Set<Reading>();
    public DbSet<HourlyAggregate> HourlyAggregates => Set<HourlyAggregate>();
    public DbSet<DirtyHour> DirtyHours => Set<DirtyHour>();
    public DbSet<ProcessedBatch> ProcessedBatches => Set<ProcessedBatch>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>().HasIndex(t => t.ApiKey).IsUnique();
        b.Entity<Meter>().HasIndex(m => m.TenantId);
        // Aggregation filters readings by meter and time range.
        b.Entity<Reading>().HasIndex(r => new { r.MeterId, r.Timestamp });
        b.Entity<HourlyAggregate>().HasIndex(h => new { h.MeterId, h.HourStart }).IsUnique();
        // One pending aggregation job is enough for a meter and hour.
        b.Entity<DirtyHour>().HasIndex(d => new { d.MeterId, d.HourStart }).IsUnique();
        // Idempotency guarantee: a batch can be processed once per tenant.
        b.Entity<ProcessedBatch>().HasIndex(p => new { p.TenantId, p.BatchId }).IsUnique();
        // Usage sorgusu tenant ve UTC saat aralığına göre filtrelenir.
        b.Entity<ProcessedBatch>().HasIndex(p => new { p.TenantId, p.CreatedAt });
        // SQL Server'da kWh değerlerinin varsayılan precision ile sessizce kesilmesini önler.
        b.Entity<Reading>().Property(r => r.Kwh).HasPrecision(18, 2);
        b.Entity<HourlyAggregate>().Property(h => h.TotalKwh).HasPrecision(18, 2);
        // Readings: index yok. Aggregate sorgusu MeterId + Timestamp ile filtreliyor.
    }
}

public static class Seed
{
    public static void Run(AppDbContext db)
    {
        if (db.Tenants.Any()) return;
        db.Tenants.AddRange(
            new Tenant { Id = "tenant-a", Name = "Tenant A", ApiKey = "key-tenant-a" },
            new Tenant { Id = "tenant-b", Name = "Tenant B", ApiKey = "key-tenant-b" });
        for (var i = 1; i <= 200; i++)
        {
            db.Meters.Add(new Meter { Id = $"MTR-A-{i:000}", TenantId = "tenant-a" });
            db.Meters.Add(new Meter { Id = $"MTR-B-{i:000}", TenantId = "tenant-b" });
        }
        db.SaveChanges();
    }
}
