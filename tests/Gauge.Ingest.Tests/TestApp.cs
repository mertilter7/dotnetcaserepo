using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Gauge.Ingest.Services;

namespace Gauge.Ingest.Tests;

/// <summary>Her test instance'ı için ayrı SQLite dosyası kullanır.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"gauge-test-{Guid.NewGuid():N}.db");
    private readonly bool _runAggregationWorker;

    public TestApp(bool runAggregationWorker = false)
    {
        _runAggregationWorker = runAggregationWorker;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={_dbPath}"
            }));

        // Focused API tests disable the worker; aggregation tests explicitly opt in.
        if (!_runAggregationWorker)
        {
            builder.ConfigureServices(services =>
            {
                var worker = services.SingleOrDefault(
                    d => d.ImplementationType == typeof(AggregationWorker));
                if (worker is not null)
                    services.Remove(worker);
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_dbPath); } catch { /* ignore */ }
    }
}
