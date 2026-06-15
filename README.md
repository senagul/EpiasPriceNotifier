# ⚡ EpiasPriceNotifier

EPİAŞ Şeffaflık Platformu'ndan saatlik elektrik takas fiyatlarını (PTF) çekip, ucuz/sıfır TL saatleri Telegram, e-posta ve ntfy.sh push bildirimleri olarak gönderen .NET 8 servisi.

**Durum:** 🚧 Aktif geliştirme — Clean Architecture iskeleti tamamlandı, ilk feature'lar yazılıyor.

## Neden Bu Proje?

Türkiye'de yüksek tüketim grubunda olan haneler için elektrik fiyatı saatlik değişiyor. Bazı saatlerde 0 TL/MWh, bazılarında 1500+ TL/MWh. Çamaşır makinesi, bulaşık makinesi, elektrikli araç şarjı gibi cihazları doğru saatlerde çalıştırmak ciddi tasarruf sağlıyor — ama EPİAŞ uygulamasını her gün açıp 24 saati incelemek zahmetli. Bu servis bunu otomatikleştiriyor.

## Mimari

[Clean Architecture](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html), bağımlılık yönü içeriye doğru:

```
Worker (Composition Root + Minimal API + Quartz)
  │
  ├──► Application (Use Cases, MediatR)
  │      │
  │      └──► Domain (Entities, Value Objects — sıfır bağımlılık)
  │
  └──► Infrastructure (EPİAŞ HTTP client, EF Core, Notification adapters)
         │
         └──► Application
```

### Öne Çıkan Tasarım Kararları

- **Global Exception Handling** — `IExceptionHandler` ile tek merkezi `GlobalExceptionHandler` sınıfı, exception tipine göre switch expression ile `ProblemDetails` (RFC 7807) response
- **Composite Notifications** — Telegram, Gmail SMTP, ntfy.sh paralel gönderim (biri patlasa diğerleri çalışır)
- **CAS/TGT Authentication** — EPİAŞ Şeffaflık API'sinin CAS protokolü, memory cache ile TGT yönetimi
- **Resilience** — Polly retry + circuit-breaker, idempotency log
- **Observability** — OpenTelemetry + SigNoz (log + trace + metrik tek UI'da)

Detaylı mimari dokümanı: [docs/Architecture.md](docs/Architecture.md)

## Teknoloji Stack

| Katman | Teknoloji |
|---|---|
| Runtime | .NET 8 LTS |
| Host | `WebApplication` (Minimal API + BackgroundService hibridi) |
| Mediator | MediatR 12 |
| Scheduling | Quartz.NET |
| HTTP | HttpClient + Polly |
| Persistence | EF Core 8 + SQLite |
| Logging | Serilog → OTLP → SigNoz |
| Telegram | Telegram.Bot |
| Email | MailKit (Gmail SMTP) |
| Push | ntfy.sh (kayıt gerektirmeyen open-source push) |
| Test | xUnit + FluentAssertions + NSubstitute + WireMock.Net |

## Çalıştırma

### Ön Koşullar
- .NET 8 SDK
- (Opsiyonel) Docker — self-hosted SigNoz için

### Yapılandırma

User secrets'a kimlik bilgilerini ekle:

```bash
cd src/EpiasPriceNotifier.Worker

dotnet user-secrets set "Epias:Username" "epias-mail@adresin.com"
dotnet user-secrets set "Epias:Password" "EPIAS_PAROLA"
dotnet user-secrets set "Telegram:BotToken" "TELEGRAM_BOT_TOKEN"
dotnet user-secrets set "Telegram:ChatId" "CHAT_ID"
dotnet user-secrets set "Email:From" "kendim@gmail.com"
dotnet user-secrets set "Email:AppPassword" "GMAIL_APP_PASSWORD"
dotnet user-secrets set "Otel:IngestionKey" "SIGNOZ_INGESTION_KEY"
```

### Build & Run

```bash
dotnet build
dotnet run --project src/EpiasPriceNotifier.Worker
```

Endpoint'ler:
- `GET /health` — health check
- `POST /trigger/fetch` — manuel fiyat çekme
- `GET /status` — son durum

## Yol Haritası

- [x] Solution scaffold (Clean Architecture, NuGet paketleri)
- [ ] EPİAŞ adapter (`CasTgtProvider`, `EpiasPriceClient`)
- [ ] Domain modelleri + `CheapHourAnalyzer`
- [ ] MediatR use case'leri
- [ ] `GlobalExceptionHandler` (IExceptionHandler)
- [ ] Bildirim kanalları (Telegram, Email, Ntfy)
- [ ] EF Core persistence + idempotency
- [ ] Quartz scheduler + cron job
- [ ] OpenTelemetry + SigNoz entegrasyonu
- [ ] Integration test'ler (WireMock.Net)
- [ ] Dockerfile + GitHub Actions CI/CD

## Lisans

MIT
