# Gauge.Ingest — Mülakat Starter Repo

Sayaç okuma ingest servisinin basitleştirilmiş hali. SQLite (`gauge.db`) kullanır, dış bağımlılık yok.

```bash
dotnet build
dotnet test
dotnet run --project src/Gauge.Ingest   # http://localhost:5000/swagger
```

Örnek istek:

```bash
curl -X POST http://localhost:5000/api/readings \
  -H "X-Api-Key: key-tenant-a" -H "Content-Type: application/json" \
  -d '{"collectorId":"col-1","readings":[{"meterId":"MTR-A-001","timestamp":"2026-09-18T10:15:00Z","kwh":1.2}]}'
```

Görevler aday dokümanındadır.
