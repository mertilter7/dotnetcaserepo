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

## Bilinen Kısıtlama — Aggregation ↔ Eşzamanlı Okuma Yarışı

`AggregationWorker`, bir saatin toplamını (`SUM`) transaction **başlamadan önce** hesaplar ve
`DirtyHour` işaretini işlem sonunda siler. Ingest tarafı ise aynı saat için lease'li bir
`DirtyHour` zaten varsa yeni işaret **eklemez** (dedup). Bu iki davranışın birleşimi dar bir
yarış penceresi yaratır:

1. Worker `(meter, saat)` kaydını claim eder ve `SUM = X` hesaplar.
2. Bu sırada aynı saate **geç/eşzamanlı bir okuma** gelir; okuma yazılır ama mevcut lease'li
   `DirtyHour` görüldüğü için yeni işaret oluşturulmaz.
3. Worker `HourlyAggregate = X` yazıp `DirtyHour`'u siler.

**Sonuç:** Ham `Reading` verisi kaybolmaz (tabloda durur), ancak türetilmiş `HourlyAggregate`
o okumayı içermez ve saat yeniden işaretlenmediği için (aynı saate başka okuma gelmedikçe)
kendiliğinden düzelmez. Bir faturalama/raporlama sistemi için bu, raporlanan değerde sessiz
bir eksik hesaplamadır.

**Neden şimdi çözülmedi (bilinçli trade-off):** Doküman kabul kriterleri (idempotency + mevcut
testler) karşılanıyor; bu yarış yalnızca yüksek eşzamanlılıkta oluşan bir uç durum ve mevcut
testler tarafından tetiklenmiyor. Kalıcı çözümü `DirtyHour`'a şema değişikliği (version/damga)
ve yeni migration gerektirir; bu da aggregation çekirdeğine dokunan, ayrı ve dikkatli ele
alınması gereken bir iştir.

**Önerilen çözüm:** `DirtyHour`'a bir `Version`/`UpdatedAt` alanı eklenir; ingest, lease'li bir
kayıt bulduğunda onu "yeniden kirlendi" olarak işaretler (version'ı artırır). Worker silmeyi
koşullu yapar: `WHERE Id=... AND LeaseId=<worker> AND Version=<claim anındaki version>`. Ingest
version'ı değiştirdiyse silme 0 satır etkiler → `DirtyHour` kalır → saat yeniden hesaplanır.
Bu yaklaşım crash-safety'yi (lease/delete-after) bozmadan eksik hesaplamayı kapatır.

## Alınan Kararlar ve Trade-off'lar

Case çözülürken alınan mimari/implementasyon kararları; her biri için **ne kazandık** ve
**karşılığında hangi maliyeti/limiti kabul ettik**.

| Karar | Kazanç | Kabul edilen maliyet / limit |
|-------|--------|------------------------------|
| Senkron aggregation → arka plan worker (`AggregationWorker`) | Düşük ingest latency, saat başı timeout'ların ortadan kalkması | **Eventual consistency**: raporlar birkaç saniye/dakika gecikebilir; worker sağlığı izlenmeli |
| Idempotency = DB unique index + transaction (in-memory cache **değil**) | 2 VM ve restart'a dayanıklı gerçek garanti; mükerrer kayıt engellenir | Her ingest'te ek DB kontrolü ve `ProcessedBatch` yazımı |
| Tenant başına rate limiter (API key partition) | Noisy-neighbor koruması; en büyük tenant diğerlerini boğamaz; `429 + Retry-After` | **Process-local** (cross-VM global değil); collector doğru retry yapmazsa gecikme; global limit için Redis ertelendi |
| DB-backed tenant quota (in-memory sayaç değil) | Çok VM tutarlılığı; restart'ta kaybolmaz; gerçek kullanım verisi | Quota okuması ek DB sorgusu (`SUM`) |
| Distributed worker lease (`LeaseId`/`LeaseUntil`) | İki VM worker'ı aynı saati işlemez; çift hesaplama önlenir | Lease süresi/atomik update yönetimi; yanlış ayarda gereksiz tekrar iş |
| Geç gelen okumada saatin **baştan** yeniden hesaplanması (delta değil) | Doğru toplam; geç okuma senaryosu düzelir | Her tetiklemede tam `SUM` maliyeti; eşzamanlılık yarışı (bkz. Bilinen Kısıtlama) |
| `DirtyHour` dedup (unique index) | Aynı meter/saat için tek aggregation işi; kuyruk şişmez | Ingest sırasında dedup kontrolü; lease'li kayıtla yarış riski |
| Provider ayrımı: SQLite (test/local) + SQL Server (prod), ayrı context & migration | Prod şeması gerçekçi doğrulanır; test izolasyonu korunur | İki migration setinin birlikte bakımı |
| İlk transaction'da tüm batch (Reading + ProcessedBatch + DirtyHour atomik) | Kısmi yazım olmaz; retry güvenli | Büyük batch'te uzun transaction / kilit riski; gerekirse chunk'lama |
| Observability: `GaugeMetrics` (ingest, duplicate, rate-limit, worker, süreler) | Üretimde görünürlük; darboğaz/hata erken tespit | Metrik toplama/altyapı (exporter) gereksinimi |
| `db.Database.Migrate()` (SQL Server) — otomatik migration | Şema versiyonlu ve tekrarlanabilir | Büyük prod tablolarında migration uzun sürebilir/kilitleyebilir; bakım penceresi önerilir |
| Collector durable spool / backoff+jitter **uygulanmadı** (kapsam dışı) | Server tarafı hazır tutuldu; gereksiz kapsam genişlemesi yok | Veri kaybı garantisi collector'a bağımlı; contract doğrulanmalı |
