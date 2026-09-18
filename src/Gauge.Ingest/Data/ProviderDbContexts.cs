using Microsoft.EntityFrameworkCore;

namespace Gauge.Ingest.Data;

// Provider'a özel context'ler migration setlerinin birbirine karışmasını engeller.
public sealed class SqliteAppDbContext(DbContextOptions<SqliteAppDbContext> options)
    : AppDbContext(options);

public sealed class SqlServerAppDbContext(DbContextOptions<SqlServerAppDbContext> options)
    : AppDbContext(options);
