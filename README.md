# Gauge.Ingest — Mülakat Starter Repo

Sayaç okuma ingest servisinin basitleştirilmiş hali. SQLite (`gauge.db`) kullanır, dış bağımlılık yok.

```bash
dotnet build
dotnet test
dotnet run --project src/Gauge.Ingest   # http://localhost:5000/swagger
```

Örnek istek (`batchId` zorunludur — idempotency anahtarı):

```bash
curl -X POST http://localhost:5000/api/readings \
  -H "X-Api-Key: key-tenant-a" -H "Content-Type: application/json" \
  -d '{"collectorId":"col-1","batchId":"11111111-1111-1111-1111-111111111111","readings":[{"meterId":"MTR-A-001","timestamp":"2026-09-18T10:15:00Z","kwh":1.2}]}'
```

Görevler aday dokümanındadır.

## Collector Contract (Kapsam Dışı — Production Handoff)

Bu repo yalnızca **server (ingest) tarafını** kapsar. Veriyi gönderen **collector** ayrı bir
bileşendir ve bu repoda yer almaz. Production'a çıkmadan önce collector ekibinden aşağıdaki
davranışların uygulandığı doğrulanmalıdır:

- `429`, timeout ve `5xx` yanıtlarında retry edilmeli.
- `400` ve `401` yanıtlarında retry **edilmemeli**.
- Retry sırasında aynı `batchId` korunmalı (idempotency için kritik).
- Yanıttaki `Retry-After` header'ına uyulmalı.
- Başarılı response alınmadan batch **silinmemeli**.
- Retry'lerde exponential backoff + jitter kullanılmalı.
- Batch, başarılı gönderime kadar durable local spool üzerinde tutulmalı.

Server tarafındaki karşılıkları **hazırdır**: `batchId` idempotency, transaction bütünlüğü,
tenant başına rate limit ve `429 + Retry-After`. Collector bu contract'a uyduğu sürece
veri kaybı yaşanmadan backpressure yönetilir. Contract doğrulanamazsa rate limiter
production'da kontrollü bir rollout ile aktif edilmelidir.
