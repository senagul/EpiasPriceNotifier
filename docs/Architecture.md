# ⚡ EPİAŞ Ucuz Saat Bildirim Sistemi — Mimari Dokümanı (v4)

> **Proje Kısa Tanımı:** EPİAŞ Şeffaflık Platformu'ndan saatlik elektrik takas fiyatlarını (PTF) çekip, kullanıcının tanımladığı eşik altındaki ucuz/sıfır TL saatleri tespit eden ve çoklu kanal üzerinden (Telegram / E-posta / Push) bildirim gönderen, .NET 8 tabanlı, Clean Architecture ile yapılandırılmış bir arka plan servisi.
>
> **Hedef:** Hem gerçek bir problemi (yüksek tüketim grubu kullanıcısının ucuz tarife saatlerini kaçırmaması) çözmek, hem de mülakat sürecinde gösterilebilecek kalitede bir portföy projesi üretmek.
>
> **v3 değişiklikleri:** Global exception handling, senagul/MinimalAPIAndGrpc repo'sundaki gibi **tek merkezi `IExceptionHandler` sınıfı** yapısına çevrildi. Worker projesine küçük bir Minimal API (health check + manuel trigger) eklendi.
>
> **v4 değişiklikleri:** **Observability (SigNoz + OpenTelemetry)** eklendi — loglar, trace'ler ve metrikler görsel olarak izlenebiliyor.

---

## 1. EPİAŞ Şeffaflık API'si Araştırması

### 1.1. Platform Genel Bilgisi

EPİAŞ (Enerji Piyasaları İşletme A.Ş.), Türkiye elektrik ve doğalgaz spot piyasalarını işleten kurumdur. **Şeffaflık Platformu 2.0**, 4 Aralık 2023'te canlıya alınmıştır ve bizim ihtiyacımız olan tüm veriyi (PTF, SMF, üretim/tüketim vb.) REST üzerinden sunar.

- **Ana sayfa:** `https://seffaflik.epias.com.tr/`
- **Teknik dokümantasyon (Swagger):** `https://seffaflik.epias.com.tr/electricity-service/technical/tr/index.html`
- **Servis base path:** `https://seffaflik.epias.com.tr/electricity-service`

> ⚠️ **Önemli:** 19 Ağustos 2024 itibarıyla API'ye erişim için **kimlik doğrulama (TGT) zorunlu** hale getirildi. Platforma e-posta + parola ile ücretsiz kayıt olmamız gerekiyor.

### 1.2. Bizim Kullanacağımız Servis: MCP (Piyasa Takas Fiyatı / PTF)

Tam ihtiyacımız olan endpoint: **Gün Öncesi Piyasası — Market Clearing Price (MCP / PTF)**.

| Özellik | Değer |
|---|---|
| Endpoint | `POST /electricity-service/v1/markets/dam/data/mcp` |
| Yayın saati | Her gün ~14:00'te ertesi günün 24 saatlik fiyatları açıklanır |
| Granülarite | Saatlik (24 değer / gün) |
| Birim | TL/MWh |
| Body | `{ "startDate": "2026-06-14T00:00:00+03:00", "endDate": "2026-06-14T00:00:00+03:00" }` |

**Örnek response (basitleştirilmiş):**
```json
{
  "items": [
    { "date": "2026-06-14T00:00:00+03:00", "hour": "00:00", "price": 0.00, "priceUsd": 0.00, "priceEur": 0.00 },
    { "date": "2026-06-14T01:00:00+03:00", "hour": "01:00", "price": 142.55, "priceUsd": 4.20, "priceEur": 3.95 },
    { "date": "2026-06-14T02:00:00+03:00", "hour": "02:00", "price": 0.00, "priceUsd": 0.00, "priceEur": 0.00 }
    // ... 24 satır
  ]
}
```

Bu tek endpoint MVP için yeterli. İleride ek servislerle (SMF, üretim, gün içi piyasası) zenginleştirilebilir.

### 1.3. Kimlik Doğrulama Akışı (CAS / TGT)

EPİAŞ, **CAS (Central Authentication Service)** kullanıyor. Akış iki adımlı:

**Adım 1 — TGT (Ticket Granting Ticket) al:**
```http
POST https://giris.epias.com.tr/cas/v1/tickets
Content-Type: application/x-www-form-urlencoded
Accept: text/plain

username=mail@adresim.com&password=PAROLA
```

> ⚠️ **6 Kasım 2025 duyurusu:** Eskiden `?username=...&password=...` query string olarak da kabul ediliyordu. **1 Aralık 2025 saat 10:00 itibarıyla bu yöntem kapatıldı** — credential'ları **mutlaka body** içinde göndermek gerekiyor.

- Response: TGT string (örn. `TGT-237-U0TU...cas02.epias.com.tr`)
- TGT geçerlilik süresi: **2 saat** (~100 dk'da bir yenilemek güvenli)
- TGT'yi sık sık yenileme — CAS sunucusu IP'yi block edebilir, **cache zorunlu**

**Adım 2 — TGT'yi header olarak ekleyip veri servisini çağır:**
```http
POST https://seffaflik.epias.com.tr/electricity-service/v1/markets/dam/data/mcp
Content-Type: application/json
TGT: TGT-237-U0TU...cas02.epias.com.tr

{ "startDate": "2026-06-14T00:00:00+03:00", "endDate": "2026-06-14T00:00:00+03:00" }
```

### 1.4. Önemli Sınırlamalar / Notlar

- **Rate limit:** Resmi belgeli değil; TGT'yi her istek için yenilersen IP banlanma riski var.
- **Tarih formatı:** ISO 8601, Türkiye saati offset'i ile (`+03:00`). Yanlış vermek boş response'a sebep oluyor.
- **Yayın gecikmesi:** Bazı günler 14:30'a kadar gecikebilir — bildirim job'ını ona göre planla (örn. 15:00).

---

## 2. Çözüm Mimarisi — Clean Architecture

### 2.1. Neden Clean Architecture?

1. **Bağımlılık yönü tek yönlü:** Domain/Application, dış framework/SDK/API'lere bağımlı değil. EPİAŞ yarın endpoint değiştirse Infrastructure'da bir adapter güncellemek yetiyor.
2. **Test edilebilirlik:** "Fiyat şu saatten ucuzsa bildir" kuralı, gerçek EPİAŞ'a hiç bağlanmadan in-memory test ile %100 doğrulanabilir.
3. **Çoklu bildirim kanalı:** `INotificationSender` arayüzünü her sağlayıcı için ayrı implement edebiliyoruz — Open/Closed prensibi.
4. **Mülakat hikayesi:** Ports & Adapters / Hexagonal, SOLID, Dependency Inversion, MediatR / CQRS gibi konuları somut örnekle anlatabilirsin.

### 2.2. Katmanlar — Solution Yapısı

> **Not:** Worker projesi aslında bir **Minimal API + BackgroundService** hibridi. Hem Quartz scheduler arka planda çalışıyor, hem de `/health`, `/trigger`, `/status` gibi minimal endpoint'ler var. Bu sayede `.NET 8`'in built-in `IExceptionHandler` middleware'ini kullanabiliyoruz.

```
EpiasPriceNotifier.sln
│
├── src/
│   ├── EpiasPriceNotifier.Domain/                ← Çekirdek iş kuralları (sıfır bağımlılık)
│   │   ├── Entities/
│   │   │   ├── DailyPriceSchedule.cs
│   │   │   ├── HourlyPrice.cs
│   │   │   └── NotificationRule.cs
│   │   ├── ValueObjects/
│   │   │   ├── PriceThreshold.cs
│   │   │   └── CheapWindow.cs
│   │   ├── Enums/
│   │   │   └── NotificationChannel.cs
│   │   ├── Events/
│   │   │   └── CheapHoursDetectedEvent.cs
│   │   └── Services/
│   │       └── ICheapHourAnalyzer.cs
│   │
│   ├── EpiasPriceNotifier.Application/           ← Use case'ler, orchestration
│   │   ├── Abstractions/
│   │   │   ├── IEpiasPriceClient.cs
│   │   │   ├── INotificationSender.cs
│   │   │   ├── IPriceRepository.cs
│   │   │   └── IDateTimeProvider.cs
│   │   ├── UseCases/
│   │   │   ├── FetchDailyPrices/
│   │   │   └── AnalyzeAndNotify/
│   │   ├── Common/
│   │   │   └── Exceptions/                       ← ★ Tüm custom exception sınıfları
│   │   │       ├── NotFoundException.cs
│   │   │       ├── ValidationException.cs
│   │   │       ├── EpiasIntegrationException.cs
│   │   │       └── NotificationSendException.cs
│   │   └── DependencyInjection.cs
│   │
│   ├── EpiasPriceNotifier.Infrastructure/        ← Dış dünya implementasyonları
│   │   ├── Epias/
│   │   │   ├── EpiasPriceClient.cs
│   │   │   ├── CasTgtProvider.cs
│   │   │   └── EpiasOptions.cs
│   │   ├── Notifications/
│   │   │   ├── TelegramNotificationSender.cs     ← Bedava: Telegram Bot API
│   │   │   ├── EmailNotificationSender.cs        ← Bedava: Gmail SMTP
│   │   │   └── NtfyNotificationSender.cs         ← Bedava: ntfy.sh push (SMS yerine)
│   │   ├── Persistence/
│   │   │   ├── AppDbContext.cs                   ← EF Core + SQLite
│   │   │   └── PriceRepository.cs
│   │   ├── Logging/
│   │   │   └── SerilogConfiguration.cs
│   │   └── DependencyInjection.cs
│   │
│   └── EpiasPriceNotifier.Worker/                ← Composition root + scheduler + minimal API
│       ├── Program.cs                            ← ★ AddExceptionHandler + UseExceptionHandler burada
│       ├── ExceptionHandlers/                    ← ★ TEK MERKEZ — global exception handling
│       │   └── GlobalExceptionHandler.cs         ← IExceptionHandler implementasyonu
│       ├── Endpoints/                            ← Minimal API endpoint'leri
│       │   ├── HealthEndpoints.cs
│       │   └── TriggerEndpoints.cs               ← Manuel fetch + son durum
│       ├── Jobs/
│       │   └── DailyPriceFetchJob.cs             ← Quartz job (içinden exception fırlatır,
│       │                                              GlobalExceptionHandler yakalar)
│       ├── appsettings.json
│       └── Dockerfile
│
└── tests/
    ├── EpiasPriceNotifier.Domain.UnitTests/
    ├── EpiasPriceNotifier.Application.UnitTests/
    └── EpiasPriceNotifier.Integration.Tests/     ← WireMock.Net ile EPİAŞ taklidi
```

### 2.3. Bağımlılık Diyagramı

```
┌─────────────────────────────────────────────────────────────────┐
│                   Worker (Composition Root)                     │
│      Program.cs + Quartz scheduler + DI + Global Handlers       │
└───────────────────────┬─────────────────────────────────────────┘
                        │ (sadece DI'da referans)
        ┌───────────────┼───────────────┐
        ▼               ▼               ▼
┌──────────────┐ ┌────────────┐ ┌───────────────┐
│ Application  │ │Infrastructure│ │   (Worker     │
│  Use Cases   │ │   Adapters  │ │   kendisi)    │
└──────┬───────┘ └──────┬─────┘ └───────────────┘
       │                │
       │ interfaces     │ implements
       └────────┬───────┘
                ▼
       ┌────────────────┐
       │     Domain     │   ← Hiçbir şeye bağımlı değil
       │ (saf iş kuralı)│
       └────────────────┘
```

**Kural:** Oklar **içeriye** doğru gider. Domain dış dünyayı bilmez.

---

## 3. ★ Global Exception Handling — Tek Merkezi `IExceptionHandler`

### 3.1. Yaklaşım

`senagul/MinimalAPIAndGrpc` repo'sundaki ile aynı felsefe: **tüm exception'lar tek bir sınıfta** karşılanır. .NET 8 ile gelen `IExceptionHandler` interface'i `TryHandleAsync` metodu içinde **switch expression** ile exception tipine göre status code + `ProblemDetails` döndürür. Use case'lerde, endpoint'lerde ve job'larda **dağınık try/catch yok** — herkes exception'ı serbestçe fırlatır, merkez yakalar.

```
┌──────────────────────────────────────────────────────────────┐
│  Bir yerden exception fırlatıldı                             │
│  (use case, endpoint handler, Quartz job, herhangi bir yer)  │
└────────────────────────┬─────────────────────────────────────┘
                         │
                         ▼
         ┌─────────────────────────────────────┐
         │   GlobalExceptionHandler            │
         │   (IExceptionHandler implementation)│
         │                                     │
         │   switch (exception)                │
         │   {                                 │
         │     NotFoundException        → 404  │
         │     ValidationException      → 400  │
         │     EpiasIntegrationException→ 502  │
         │     NotificationSendException→ 503  │
         │     _                        → 500  │
         │   }                                 │
         │                                     │
         │   1. Logla (Serilog)                │
         │   2. ProblemDetails response yaz    │
         │   3. Kritikse Telegram'a alarm at   │
         └─────────────────────────────────────┘
```

Bu yapıyı kullanabilmek için Worker projesini **Minimal API + BackgroundService hibridi** olarak kuruyoruz. `WebApplication.CreateBuilder(args)` kullanıyor; bu sayede `IExceptionHandler` middleware'i hem HTTP endpoint'leri için, hem de job'lardan yayılan exception'lar için (aşağıda anlatılan ufak bir bridge ile) kullanılabilir.

### 3.2. Custom Exception Sınıfları

`Application/Common/Exceptions/` altında, sade ve odaklı:

```csharp
// NotFoundException.cs
public sealed class NotFoundException : Exception
{
    public NotFoundException(string entity, object key)
        : base($"{entity} bulunamadı (key: {key})") { }
}

// ValidationException.cs
public sealed class ValidationException : Exception
{
    public IDictionary<string, string[]> Errors { get; }
    
    public ValidationException(IDictionary<string, string[]> errors)
        : base("Validation hatası")
        => Errors = errors;
}

// EpiasIntegrationException.cs
public sealed class EpiasIntegrationException : Exception
{
    public int? StatusCode { get; }
    
    public EpiasIntegrationException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
        => StatusCode = statusCode;
}

// NotificationSendException.cs
public sealed class NotificationSendException : Exception
{
    public string Channel { get; }
    
    public NotificationSendException(string channel, string message, Exception? inner = null)
        : base(message, inner)
        => Channel = channel;
}
```

Use case'ler bu exception'ları hiç try/catch'siz fırlatır:

```csharp
public async Task<DailyPriceSchedule> Handle(FetchDailyPricesQuery query, CancellationToken ct)
{
    var schedule = await _repo.GetByDateAsync(query.Date, ct);
    if (schedule is null)
        throw new NotFoundException(nameof(DailyPriceSchedule), query.Date);
    return schedule;
}
```

### 3.3. `GlobalExceptionHandler` (Tek Merkez)

`Worker/ExceptionHandlers/GlobalExceptionHandler.cs`:

```csharp
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using EpiasPriceNotifier.Application.Common.Exceptions;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly INotificationSender _criticalAlertSender;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger,
        INotificationSender criticalAlertSender)
    {
        _logger = logger;
        _criticalAlertSender = criticalAlertSender;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // 1. Exception tipine göre status + title belirle
        var (statusCode, title) = MapException(exception);

        // 2. Logla — kritiklik seviyesi de exception'a göre
        LogException(exception, statusCode);

        // 3. 5xx hataları için kullanıcıya Telegram alarm gönder
        if (statusCode >= 500)
        {
            _ = SendCriticalAlertAsync(exception, cancellationToken); // fire-and-forget
        }

        // 4. RFC 7807 ProblemDetails formatında response yaz
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Type = exception.GetType().Name,
            Title = title,
            Detail = exception.Message,
            Instance = httpContext.Request.Path
        };

        // ValidationException özel durumu: alan bazlı hata listesi
        if (exception is ValidationException validationEx)
        {
            problemDetails.Extensions["errors"] = validationEx.Errors;
        }

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true; // Bu exception handle edildi, başka handler'a gerek yok
    }

    private static (int StatusCode, string Title) MapException(Exception exception) =>
        exception switch
        {
            NotFoundException              => (StatusCodes.Status404NotFound,           "Kayıt bulunamadı"),
            ValidationException            => (StatusCodes.Status400BadRequest,         "Geçersiz istek"),
            UnauthorizedAccessException    => (StatusCodes.Status401Unauthorized,       "Yetkisiz erişim"),
            EpiasIntegrationException epi  => (StatusCodes.Status502BadGateway,         $"EPİAŞ entegrasyon hatası ({epi.StatusCode})"),
            NotificationSendException      => (StatusCodes.Status503ServiceUnavailable, "Bildirim gönderilemedi"),
            TaskCanceledException          => (StatusCodes.Status408RequestTimeout,     "İstek zaman aşımına uğradı"),
            _                              => (StatusCodes.Status500InternalServerError,"Beklenmedik bir hata oluştu")
        };

    private void LogException(Exception exception, int statusCode)
    {
        // 4xx → warning, 5xx → error
        if (statusCode >= 500)
            _logger.LogError(exception, "Unhandled exception ({StatusCode}): {Message}",
                statusCode, exception.Message);
        else
            _logger.LogWarning(exception, "Handled exception ({StatusCode}): {Message}",
                statusCode, exception.Message);
    }

    private async Task SendCriticalAlertAsync(Exception ex, CancellationToken ct)
    {
        try
        {
            var body = $"""
                Tip: {ex.GetType().Name}
                Mesaj: {ex.Message}
                Zaman: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
                """;
            await _criticalAlertSender.SendAsync("🚨 EpiasNotifier — Kritik Hata", body, ct);
        }
        catch
        {
            // Alarm gönderiminde de patlasa, ana akışı bozma
        }
    }
}
```

### 3.4. `Program.cs` — DI Registration

```csharp
var builder = WebApplication.CreateBuilder(args);

// Serilog
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext());

// Katmanlar
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Quartz scheduler
builder.Services.AddQuartzScheduling(builder.Configuration);

// ★ Global Exception Handler — TEK satır
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// ★ Middleware pipeline'a ekle
app.UseExceptionHandler();

// Minimal API endpoint'leri
app.MapHealthEndpoints();
app.MapTriggerEndpoints();

app.Run();
```

`AddExceptionHandler<T>` ile sınıfı DI'a kaydediyorsun, `UseExceptionHandler()` ile middleware'i pipeline'a sokuyorsun. Tek satır, hepsi bu.

### 3.5. Minimal API Endpoint'leri (Exception'ları Test Etmek İçin de Faydalı)

`Worker/Endpoints/HealthEndpoints.cs`:

```csharp
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", time = DateTime.UtcNow }))
           .WithName("HealthCheck");
        return app;
    }
}
```

`Worker/Endpoints/TriggerEndpoints.cs`:

```csharp
public static class TriggerEndpoints
{
    public static IEndpointRouteBuilder MapTriggerEndpoints(this IEndpointRouteBuilder app)
    {
        // Manuel fiyat çekme — Quartz job'u beklemeden test için
        app.MapPost("/trigger/fetch", async (IMediator mediator, CancellationToken ct) =>
        {
            // try/catch YOK — exception fırlarsa GlobalExceptionHandler yakalar
            await mediator.Send(new FetchDailyPricesCommand(DateOnly.FromDateTime(DateTime.Today)), ct);
            return Results.Accepted();
        })
        .WithName("ManualFetch");

        // Son durum
        app.MapGet("/status", async (IPriceRepository repo, CancellationToken ct) =>
        {
            var latest = await repo.GetLatestAsync(ct);
            if (latest is null)
                throw new NotFoundException(nameof(DailyPriceSchedule), "latest");
            return Results.Ok(latest);
        })
        .WithName("Status");

        return app;
    }
}
```

Dikkat: handler'larda **hiç try/catch yok**. Exception fırlarsa middleware yakalar.

### 3.6. Background Job'lardan Bridge

Quartz job'u HTTP context'i içinde çalışmıyor → `IExceptionHandler` middleware'i otomatik tetiklenmiyor. Bunun için **basit bir adapter** yazıyoruz: job içinde exception olursa, doğrudan `GlobalExceptionHandler`'ın **paylaşılan mantığını** çağırıyoruz.

İki yöntem var; ikincisi daha temiz:

**Yöntem A (basit):** Job içinde direkt try/catch + handler'a delegate et.

```csharp
public sealed class DailyPriceFetchJob : IJob
{
    private readonly IMediator _mediator;
    private readonly GlobalExceptionHandler _handler;
    private readonly ILogger<DailyPriceFetchJob> _logger;

    public async Task Execute(IJobExecutionContext ctx)
    {
        try
        {
            await _mediator.Send(new FetchDailyPricesCommand(...), ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            // GlobalExceptionHandler'ın mantığını yeniden kullan
            await _handler.HandleBackgroundExceptionAsync(ex, ctx.JobDetail.Key.Name, ctx.CancellationToken);
            // Quartz'a tekrar deneme sinyali ver (opsiyonel)
            throw new JobExecutionException(ex) { RefireImmediately = false };
        }
    }
}
```

Bunun için `GlobalExceptionHandler`'a logla + alarm gönder kısmını ortak metoda çıkarıyoruz:

```csharp
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    // ... (yukarıdaki kod)

    // ★ Background job'lar bunu çağırır — HTTP response kısmı yok
    public async Task HandleBackgroundExceptionAsync(
        Exception exception, string source, CancellationToken ct)
    {
        var (statusCode, _) = MapException(exception);
        LogException(exception, statusCode);
        if (statusCode >= 500)
            await SendCriticalAlertAsync(exception, ct);
    }
}
```

**Yöntem B (daha clean — Decorator):** Tüm job'ları saran bir `ExceptionHandlingJob` decorator yazıp Quartz'a onu register edersin. Job'lar exception fırlatmaktan başka bir şey yapmaz. Sprint S6'da bu yöntemi tercih edebilirsin — `MediatR`'ın pipeline behavior mantığının job dünyasındaki karşılığı.

### 3.7. ProblemDetails Response Örneği

Bir endpoint'e geçersiz tarih ile istek atılsa:

```http
POST /trigger/fetch?date=invalid HTTP/1.1
```

Yanıt:

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{
  "type": "ValidationException",
  "title": "Geçersiz istek",
  "status": 400,
  "detail": "Validation hatası",
  "instance": "/trigger/fetch",
  "errors": {
    "Date": ["Geçerli bir tarih girin (yyyy-MM-dd)"]
  }
}
```

EPİAŞ erişilemediğinde:

```http
HTTP/1.1 502 Bad Gateway
Content-Type: application/problem+json

{
  "type": "EpiasIntegrationException",
  "title": "EPİAŞ entegrasyon hatası (504)",
  "status": 502,
  "detail": "EPİAŞ timeout",
  "instance": "/trigger/fetch"
}
```

Aynı anda Telegram'a düşen alarm:

```
🚨 EpiasNotifier — Kritik Hata

Tip: EpiasIntegrationException
Mesaj: EPİAŞ timeout
Zaman: 2026-06-14 12:00:15 UTC
```

### 3.8. Avantajları (Eski 5-Katman Yaklaşımına Göre)

- **Tek dosya, tek sorumluluk** — Hata mantığı dağılmıyor, `GlobalExceptionHandler.cs` aç, hepsini gör.
- **Use case'ler ve endpoint'ler tertemiz** — try/catch yok, sadece iş mantığı.
- **`.NET 8` standart yolu** — Microsoft'un önerdiği, modern ASP.NET pattern'i. Yeni bir geliştirici gelince "evet bu standart yapı" diyor.
- **RFC 7807 ProblemDetails** — Postman / Swagger / Frontend tarafı standart bir hata formatı bekliyor.
- **Yeni exception tipi eklemek kolay** — `MapException`'a bir `switch` arm ekle, bitti.

---

## 4. ★ Tamamen Ücretsiz Bildirim Stack'i

Tüm bildirim kanalları **sıfır maliyet** ile, **Türkiye'den çalışacak şekilde** seçildi.

### 4.1. Karar Matrisi

| Kanal | Önerilen Sağlayıcı | Maliyet | Limit | Türkiye'de Çalışır mı? |
|---|---|---|---|---|
| **Telegram** | Telegram Bot API | %100 ücretsiz | Yok | ✅ Evet |
| **E-posta** | Gmail SMTP + App Password | %100 ücretsiz | 500 mail/gün | ✅ Evet |
| **Push (SMS yerine)** | ntfy.sh | %100 ücretsiz | Makul kullanım | ✅ Evet |
| **WhatsApp (opsiyonel)** | CallMeBot | %100 ücretsiz (kişisel kullanım) | Düşük rate | ✅ Evet |
| **Yedek kanal** | Discord webhook | %100 ücretsiz | Yok | ✅ Evet |

> 💡 **SMS hakkında not:** Türkiye'ye gerçek SMS göndermek için **tamamen bedava** bir yol yok. Twilio trial $15 kredi verir ama yurtdışı operatör fiyatlarıyla 1 ay sürmez. Gerçek SMS deneyimine bedava en yakın çözüm **ntfy.sh push notification**'dır — telefonuna SMS gibi düşer, ücretsizdir, ve gizliliğin korunur. Bu projede SMS yerine ntfy öneriyorum.

### 4.2. Telegram Bot — Sıfır Maliyet, En Kolay

**Kurulum (5 dakika):**
1. Telegram'da `@BotFather`'ı aç → `/newbot` → bot adı ver → **token al**
2. Bot'unla bir mesajlaş (`/start`)
3. Tarayıcıdan `https://api.telegram.org/bot<TOKEN>/getUpdates` aç → `chat.id`'yi kop
4. `appsettings.json`'a yaz, bitti.

**Kod (NuGet: `Telegram.Bot`):**
```csharp
public sealed class TelegramNotificationSender : INotificationSender
{
    private readonly ITelegramBotClient _bot;
    private readonly TelegramOptions _opts;

    public async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        var text = $"*{subject}*\n\n{body}";
        try
        {
            await _bot.SendTextMessageAsync(
                chatId: _opts.ChatId,
                text: text,
                parseMode: ParseMode.Markdown,
                cancellationToken: ct);
        }
        catch (ApiRequestException ex)
        {
            throw new NotificationSendException("Telegram", ex.Message, ex);
        }
    }
}
```

**Limitler:** 30 mesaj/saniye (chat başına 1). Bu proje için sonsuz sayılır.

### 4.3. E-posta — Gmail SMTP + App Password

**Neden Gmail?** Tamamen ücretsiz, kayıt zaten var, SendGrid/Brevo gibi 3rd party'lere değil kendi domain'ine güveniyorsun.

**Kurulum:**
1. Google hesabında **2-Step Verification** açık olmalı (zorunlu).
2. https://myaccount.google.com/apppasswords adresine git → "App password" oluştur ("EpiasNotifier" gibi bir isim ver) → **16 haneli parolayı kopyala**.
3. Bu parolayı user secrets'a yaz, normal Gmail şifreni KULLANMA.

**Kod (NuGet: `MailKit`):**
```csharp
public sealed class EmailNotificationSender : INotificationSender
{
    private readonly EmailOptions _opts;

    public async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_opts.From));
        message.To.Add(MailboxAddress.Parse(_opts.To));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        try
        {
            using var smtp = new SmtpClient();
            await smtp.ConnectAsync("smtp.gmail.com", 587, 
                MailKit.Security.SecureSocketOptions.StartTls, ct);
            await smtp.AuthenticateAsync(_opts.From, _opts.AppPassword, ct);
            await smtp.SendAsync(message, ct);
            await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            throw new NotificationSendException("Email", ex.Message, ex);
        }
    }
}
```

**Limit:** Gmail SMTP: 500 mesaj/gün. Günde 1 mail göndereceğiz, fazlasıyla yeter.

**Alternatif (eğer Gmail kayıt sorunu olursa):** [Brevo](https://www.brevo.com/) — 300 mail/gün ücretsiz, SMTP credentials veriyor.

### 4.4. ntfy.sh — SMS'in Bedava Alternatifi

**ntfy.sh** açık kaynak push notification servisi. Bir konuya (`topic`) HTTP POST atınca, o konuya abone olan telefonuna **anında push** geliyor. SMS bildirimine çok yakın bir UX.

**Kurulum (2 dakika):**
1. Telefonuna `ntfy` uygulamasını indir (App Store / Play Store, ücretsiz).
2. Uygulamadan rastgele tahmin edilmesi zor bir topic seç: `epias-cheap-hours-x9k2m` gibi.
3. Subscribe ol.
4. Bitti — herhangi bir kayıt, hesap, API key yok.

**Kod (sadece HttpClient, NuGet bile gerekmez):**
```csharp
public sealed class NtfyNotificationSender : INotificationSender
{
    private readonly HttpClient _http;
    private readonly NtfyOptions _opts;

    public async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, 
            $"https://ntfy.sh/{_opts.Topic}");
        req.Headers.Add("Title", subject);
        req.Headers.Add("Priority", "high");
        req.Headers.Add("Tags", "zap"); // ⚡ emoji
        req.Content = new StringContent(body, Encoding.UTF8, "text/plain");

        try
        {
            var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new NotificationSendException("Ntfy", ex.Message, ex);
        }
    }
}
```

**Avantajları:**
- Hiç hesap/API key/limit yok
- Self-host edilebilir (Docker image var) → tam gizlilik
- Telefonuna SMS gibi düşer, kilit ekranında görünür
- Bedava ve open source

### 4.5. Multi-Channel Strateji (Composite Pattern)

Aynı bildirimi birden fazla kanala atmak için Composite:

```csharp
public sealed class CompositeNotificationSender : INotificationSender
{
    private readonly IEnumerable<INotificationSender> _senders;
    private readonly ILogger<CompositeNotificationSender> _logger;

    public async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        // Hepsini paralel gönder, biri patlasa diğerleri etkilenmesin
        var tasks = _senders.Select(async sender =>
        {
            try { await sender.SendAsync(subject, body, ct); }
            catch (NotificationSendException ex)
            {
                _logger.LogError(ex, 
                    "Bildirim başarısız: {Channel}", ex.Channel);
                // Yutuyoruz — bir kanal patlasa diğerleri çalışsın
            }
        });
        await Task.WhenAll(tasks);
    }
}
```

Böylece Telegram down olsa bile email ve ntfy gider — **fault tolerance**.

## 5. ★ Observability — SigNoz ile Log/Trace/Metrik Görselleştirme

### 5.1. SigNoz Nedir, Neden?

**SigNoz**, OpenTelemetry-native bir observability platformu — DataDog/New Relic'in open-source alternatifi. Tek bir UI'da **log**, **trace** ve **metrik**'i bir arada gösteriyor. Bizim projede iki büyük katkısı olacak:

1. **Log görselleştirme:** Konsoldaki Serilog log'larını grafiksel olarak filtrele, ara, dashboard kur. "Bu hafta kaç tane `EpiasIntegrationException` aldım?" sorusunu 5 saniyede yanıtla.
2. **Distributed tracing:** Bir HTTP isteği veya Quartz job tetiklemesi → MediatR handler → EPİAŞ HTTP çağrısı → Telegram gönderimi zincirinin tamamı **tek bir trace** olarak görünüyor. Hangi adımda kaç ms kaybettiğin net.

### 5.2. SigNoz'u Çalıştırma — İki Seçenek

**Seçenek A — SigNoz Cloud (en kolay, free tier var):**
1. https://signoz.io/teams/ adresinden kayıt ol → free trial / community tier
2. Dashboard'dan **Ingestion Key** + **region** bilgisini al
3. `appsettings.json`'a yaz, bitti. (Aşağıda kod var.)

**Seçenek B — Self-hosted (Docker Compose ile lokal):**

`docker-compose.signoz.yml` (proje kök dizininde):
```yaml
# SigNoz'un resmi docker-compose'unun kısaltılmış hali
# Gerçekte: git clone https://github.com/SigNoz/signoz.git
# cd signoz/deploy/docker-compose && docker compose up -d
```

Self-host'ta SigNoz UI `http://localhost:3301`'de, OTLP gRPC endpoint'i `http://localhost:4317`'de çalışır. Lokal geliştirmede tercih edebilirsin; production benzeri ortamda SigNoz Cloud daha kolay.

### 5.3. NuGet Paketleri

`EpiasPriceNotifier.Worker` projesine eklenir:

```bash
# Core OpenTelemetry SDK + hosting
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol

# Otomatik instrumentation'lar
dotnet add package OpenTelemetry.Instrumentation.AspNetCore   # HTTP endpoint'leri
dotnet add package OpenTelemetry.Instrumentation.Http         # HttpClient (EPİAŞ çağrıları)
dotnet add package OpenTelemetry.Instrumentation.Runtime      # GC, thread pool metrikleri

# Serilog → OpenTelemetry sink
dotnet add package Serilog.Sinks.OpenTelemetry
```

### 5.4. `Program.cs` — OpenTelemetry Kurulumu

```csharp
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

var builder = WebApplication.CreateBuilder(args);

// SigNoz endpoint ve servis bilgisi
var otlpEndpoint = builder.Configuration["Otel:Endpoint"]!;       // örn: https://ingest.eu.signoz.cloud:443
var ingestionKey = builder.Configuration["Otel:IngestionKey"]!;   // SigNoz Cloud için
var serviceName  = "EpiasPriceNotifier";
var serviceVersion = "1.0.0";

var resourceAttributes = new Dictionary<string, object>
{
    ["service.name"]        = serviceName,
    ["service.version"]     = serviceVersion,
    ["deployment.environment"] = builder.Environment.EnvironmentName
};

// OTLP header (SigNoz Cloud ingestion key için)
var otlpHeaders = $"signoz-ingestion-key={ingestionKey}";

// ─── 1) Serilog: console + OpenTelemetry sink (logs) ──────────────
builder.Host.UseSerilog((ctx, sp, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service.name", serviceName)
    .WriteTo.Console()
    .WriteTo.OpenTelemetry(o =>
    {
        o.Endpoint = otlpEndpoint;
        o.Protocol = OtlpProtocol.Grpc;       // veya HttpProtobuf
        o.Headers = new Dictionary<string, string>
        {
            ["signoz-ingestion-key"] = ingestionKey
        };
        o.ResourceAttributes = resourceAttributes;
    }));

// ─── 2) OpenTelemetry: traces + metrics ───────────────────────────
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(serviceName, serviceVersion: serviceVersion)
        .AddAttributes(resourceAttributes))
    .WithTracing(tracing => tracing
        .AddSource("EpiasPriceNotifier.*")          // Kendi custom ActivitySource'larımız
        .AddAspNetCoreInstrumentation()              // /health, /trigger endpoint'leri otomatik trace
        .AddHttpClientInstrumentation()              // EPİAŞ HttpClient çağrıları otomatik trace
        .AddOtlpExporter(opt =>
        {
            opt.Endpoint = new Uri(otlpEndpoint);
            opt.Headers = otlpHeaders;
        }))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()                 // .NET runtime metrikleri (GC, threads)
        .AddMeter("EpiasPriceNotifier.*")           // Custom metric'lerimiz
        .AddOtlpExporter(opt =>
        {
            opt.Endpoint = new Uri(otlpEndpoint);
            opt.Headers = otlpHeaders;
        }));

// ... diğer service registration'lar (Application, Infrastructure, Quartz, ExceptionHandler)

var app = builder.Build();
app.UseExceptionHandler();
app.MapHealthEndpoints();
app.MapTriggerEndpoints();
app.Run();
```

**Önemli:** Self-hosted SigNoz kullanıyorsan `Otel:Endpoint = "http://localhost:4317"` ve `IngestionKey` header'ını göndermesen de olur (lokal Collector kabul ediyor).

### 5.5. Custom ActivitySource — İş Mantığını da Trace Et

Otomatik instrumentation HTTP/ASP.NET'i sarıyor ama **bizim iş adımlarımız** (TGT alma, ucuz saat analizi, bildirim gönderimi) için custom span açmamız lazım. Tek bir static class:

`Infrastructure/Telemetry/AppDiagnostics.cs`:
```csharp
public static class AppDiagnostics
{
    public const string ServiceName = "EpiasPriceNotifier";
    public static readonly ActivitySource ActivitySource = new("EpiasPriceNotifier.Core");
    public static readonly Meter Meter = new("EpiasPriceNotifier.Metrics");

    // Counter'lar
    public static readonly Counter<long> PricesFetched =
        Meter.CreateCounter<long>("epias.prices.fetched", description: "Çekilen günlük fiyat sayısı");

    public static readonly Counter<long> NotificationsSent =
        Meter.CreateCounter<long>("notifications.sent", description: "Gönderilen bildirim sayısı");

    public static readonly Counter<long> ExceptionsHandled =
        Meter.CreateCounter<long>("exceptions.handled", description: "GlobalExceptionHandler'ın yakaladığı exception sayısı");

    // Histogram
    public static readonly Histogram<double> EpiasRequestDuration =
        Meter.CreateHistogram<double>("epias.request.duration", "ms", "EPİAŞ HTTP çağrı süresi");
}
```

Use case içinde kullanım:
```csharp
public async Task Handle(FetchDailyPricesCommand cmd, CancellationToken ct)
{
    using var activity = AppDiagnostics.ActivitySource.StartActivity("FetchDailyPrices");
    activity?.SetTag("price.date", cmd.Date.ToString("yyyy-MM-dd"));

    var prices = await _epias.GetMcpAsync(cmd.Date, ct);
    
    AppDiagnostics.PricesFetched.Add(prices.Count);
    activity?.SetTag("price.count", prices.Count);
    activity?.SetStatus(ActivityStatusCode.Ok);
}
```

`GlobalExceptionHandler`'a da bir satır ekle — yakalanan exception'ları metric olarak say:
```csharp
private void LogException(Exception exception, int statusCode)
{
    AppDiagnostics.ExceptionsHandled.Add(1,
        new KeyValuePair<string, object?>("exception.type", exception.GetType().Name),
        new KeyValuePair<string, object?>("status.code", statusCode));
    // ... log yazımı
}
```

### 5.6. Log ↔ Trace Otomatik Bağlantı

OpenTelemetry .NET SDK, **aktif Activity** içindeyken yazılan tüm log'lara `TraceId` ve `SpanId` otomatik ekliyor. SigNoz UI'da bir log satırına tıklayınca **"View Trace"** butonu çıkıyor → o log'un parçası olduğu request'in tüm zincirini görüyorsun.

Akış:
```
[Quartz tetiklenir] → trace başlar (TraceId: abc-123)
    [DailyPriceFetchJob.Execute]          ← span 1
        [FetchDailyPricesHandler]         ← span 2
            [EPİAŞ HTTP POST]             ← span 3 (HttpClient instrumentation otomatik)
        [AnalyzeAndNotifyHandler]         ← span 4
            [Telegram SendMessage]        ← span 5 (HttpClient otomatik)
            [Email Send (MailKit)]        ← span 6 (manual ActivitySource)
            [Ntfy POST]                   ← span 7
```

SigNoz'da bu trace'in **gantt chart**'ını görüyorsun — her span'ın süresi, hangisinde yavaşlama olduğu net.

### 5.7. SigNoz UI'da Ne Göreceksin

- **Logs Explorer**
  - Filtre: `severity=error AND service.name=EpiasPriceNotifier`
  - Aggregate: `count by exception.type` → hangi exception kaç kez olmuş, bar chart
- **Traces Explorer**
  - "Son 1 saatte 5xx ile biten istekler" tek tıkla
  - Service map: EpiasPriceNotifier → seffaflik.epias.com.tr → api.telegram.org
- **Dashboards**
  - "Günlük çekilen fiyat sayısı" (custom counter)
  - "EPİAŞ p95 latency" (histogram)
  - "Telegram bildirim başarı oranı"
- **Alerts**
  - SigNoz'dan: "Son 10 dakikada 5xx > 5" → Slack/email alarm. Bizim handler'ımız Telegram'a anlık atıyor zaten; SigNoz alert'i bunun yedeği.

### 5.8. Configuration (appsettings.json'a Eklenecek)

```json
"Otel": {
  "Endpoint": "https://ingest.eu.signoz.cloud:443",
  "IngestionKey": "secret-via-user-secrets",
  "ServiceName": "EpiasPriceNotifier",
  "ServiceVersion": "1.0.0"
}
```

Self-host için:
```json
"Otel": {
  "Endpoint": "http://localhost:4317",
  "IngestionKey": ""
}
```

User-secrets:
```bash
dotnet user-secrets set "Otel:IngestionKey" "SIGNOZ_INGESTION_KEY"
```

### 5.9. Sprint'lere Eklenecek

| Sprint | Çıktı | Süre |
|---|---|---|
| **S11** — OpenTelemetry kurulumu | NuGet'ler, `Program.cs` OTel pipeline, OTLP exporter | 0.5 gün |
| **S12** — Custom telemetri | `AppDiagnostics` static class, use case'lere `ActivitySource` ve metric eklenmesi | 0.5 gün |
| **S13** — SigNoz dashboard | SigNoz Cloud / self-host kurulumu, dashboard panelleri, alert kuralları | 0.5 gün |

### 5.10. Mülakat Hikayesine Yeni Madde

> *"Observability için OpenTelemetry SDK + SigNoz kullandım. Serilog log'ları OTLP üzerinden gidiyor, ASP.NET Core ve HttpClient otomatik instrumentation'la EPİAŞ HTTP çağrılarım trace'leniyor, custom ActivitySource ile iş adımlarımı (ucuz saat analizi, bildirim gönderimi) ek span olarak ekliyorum. Log'lar TraceId ile otomatik trace'lere bağlanıyor — SigNoz UI'da bir hata log'una tıklayınca o request'in tüm zincirini gantt chart olarak görüyorum. Ayrıca `GlobalExceptionHandler`'ın yakaladığı exception sayısını custom Counter olarak metriklediğim için, exception trendi dashboard'da grafik."*

---

---

## 6. Teknoloji Seçimleri (Güncel)

| Katman | Teknoloji | Neden? |
|---|---|---|
| Runtime | **.NET 8 LTS** | LTS, modern C#, `IExceptionHandler` built-in |
| Host | **`WebApplication` + Worker hibridi** | Minimal API endpoint'leri + Quartz BackgroundService aynı process'te |
| **Exception** | **`IExceptionHandler` + ProblemDetails** | ★ Tek sınıfta global handling, .NET 8 standart yolu |
| Scheduling | **Quartz.NET** | Cron, misfire policy |
| HTTP | **HttpClient + IHttpClientFactory + Polly** | Connection pool, retry, circuit-breaker |
| Mediator | **MediatR** | CQRS, use case orchestration |
| Validation | **FluentValidation** | `ValidationException` fırlat → handler 400 döner |
| Persistence | **EF Core + SQLite** | Tek dosya, container-friendly |
| Logging | **Serilog** + Console + OTel sink | Structured log, SigNoz'a OTLP üzerinden |
| **Observability** | **OpenTelemetry SDK + SigNoz** | ★ Log + Trace + Metrik tek UI'da, otomatik instrumentation |
| Config | **Options pattern + appsettings + User Secrets** | Credentials repo'ya sızmasın |
| Telegram | **Telegram.Bot** NuGet | Async, olgun |
| Email | **MailKit** (Gmail SMTP) | Modern, doğru çalışan |
| Push | **ntfy.sh** + plain HttpClient | Sıfır maliyet, sıfır kayıt |
| Test | **xUnit + FluentAssertions + NSubstitute + WireMock.Net** | EPİAŞ taklidi + handler testleri |
| CI/CD | **GitHub Actions** + Docker | Push'ta test + build + image |

### 5.1. Quartz Cron

```csharp
.WithCronSchedule("0 0 15 * * ?")  // Her gün 15:00 (PTF 14:00'te yayınlanır)
.WithMisfireHandlingInstructionFireAndProceed()
```

---

## 7. Configuration Örneği (appsettings.json)

```json
{
  "Epias": {
    "BaseUrl": "https://seffaflik.epias.com.tr/electricity-service",
    "CasUrl": "https://giris.epias.com.tr/cas/v1/tickets",
    "Username": "secret-via-user-secrets",
    "Password": "secret-via-user-secrets",
    "TgtCacheMinutes": 100
  },
  "NotificationRules": [
    {
      "Name": "Bedava Saatler",
      "ThresholdTry": 0.01,
      "Channels": [ "Telegram", "Email", "Ntfy" ]
    },
    {
      "Name": "Ucuz Saatler",
      "ThresholdTry": 300,
      "Channels": [ "Telegram", "Ntfy" ]
    }
  ],
  "Telegram": { 
    "BotToken": "secret-via-user-secrets", 
    "ChatId": "secret-via-user-secrets" 
  },
  "Email": { 
    "Smtp": "smtp.gmail.com", 
    "Port": 587, 
    "From": "secret-via-user-secrets",
    "AppPassword": "secret-via-user-secrets",
    "To": "kendim@gmail.com" 
  },
  "Ntfy": { 
    "BaseUrl": "https://ntfy.sh",
    "Topic": "epias-cheap-hours-x9k2m" 
  },
  "Schedule": { "Cron": "0 0 15 * * ?" },
  "Otel": {
    "Endpoint": "https://ingest.eu.signoz.cloud:443",
    "IngestionKey": "secret-via-user-secrets",
    "ServiceName": "EpiasPriceNotifier",
    "ServiceVersion": "1.0.0"
  },
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "File", "Args": { "path": "logs/log-.txt", "rollingInterval": "Day" } }
    ]
  }
}
```

### Critical Error Webhook (örnek Telegram alarm)

Tüm `LogLevel >= Error` olan loglar şöyle düşer:

```
🚨 [EpiasPriceNotifier] CRITICAL
CorrelationId: a3f8e9d2-...
EPIAS.INTEGRATION_FAILED
EPİAŞ erişilemedi (network): Connection timeout
```

---

## 8. Akış Senaryoları

### 7.1. Mutlu Yol (Happy Path) — Scheduled Job

```
Quartz [15:00] ──▶ DailyPriceFetchJob
                         │
                         ▼
                  IMediator.Send(FetchDailyPricesCommand)
                         │
                         ▼
                  FetchDailyPricesHandler
                         │
                         ▼
                  IEpiasPriceClient.GetMcpAsync()
                     ├─ CasTgtProvider (cache hit)
                     └─ POST /v1/markets/dam/data/mcp (Polly retry)
                         │
                         ▼
                  IMediator.Send(AnalyzeAndNotifyCommand)
                         │
                         ▼
                  ICheapHourAnalyzer (saf domain logic)
                         │
                         ▼
                  CompositeNotificationSender
                     ├─ TelegramNotificationSender
                     ├─ EmailNotificationSender
                     └─ NtfyNotificationSender
                     (paralel, biri patlasa devam eder)
```

### 7.2. Hata Yolu (EPİAŞ Down) — HTTP Endpoint'ten Manuel Trigger

```
Kullanıcı: POST /trigger/fetch
                  │
                  ▼
            ManualFetch Endpoint
                  │
                  ▼ (try/catch YOK)
            IMediator.Send(FetchDailyPricesCommand)
                  │
                  ▼
            IEpiasPriceClient (Polly 3 retry, hepsi başarısız)
                  │
                  ▼ ❌ HttpRequestException → 
                       EpiasIntegrationException fırlatılır
                  │
                  ▼
            ★ GlobalExceptionHandler.TryHandleAsync()
                  │
                  ├─ MapException → (502, "EPİAŞ entegrasyon hatası")
                  ├─ Logger.LogError(...)
                  ├─ Telegram alarm gönder (fire-and-forget)
                  └─ HTTP response yaz:
                     HTTP/1.1 502 Bad Gateway
                     Content-Type: application/problem+json
                     { "type": "EpiasIntegrationException", ... }
```

### 7.3. Hata Yolu — Job İçinden

```
Quartz [15:00] ──▶ DailyPriceFetchJob.Execute()
                         │
                         ▼ try { ... }
                  IMediator.Send → patladı
                         │
                         ▼ catch (Exception ex)
                  GlobalExceptionHandler
                     .HandleBackgroundExceptionAsync(ex, jobName)
                     ├─ Aynı switch ile log seviyesi belirle
                     ├─ 5xx ise Telegram alarm gönder
                     └─ (HTTP response yok — job context'i)
                         │
                         ▼
                  JobExecutionException fırlat (RefireImmediately=false)
                         │
                         ▼
                  Quartz: bir sonraki cron tetiklemesinde tekrar dene
```

Her iki akışta da hata yönetimi **tek bir sınıfta** — `GlobalExceptionHandler`.

---

## 9. Geliştirme Yol Haritası (Sprint Planı)

| Sprint | Çıktı | Süre |
|---|---|---|
| **S0** — Bootstrap | Solution, projeler, klasörler, boş interface'ler, GitHub repo, README | 0.5 gün |
| **S1** — EPİAŞ adapter | `CasTgtProvider` + `EpiasPriceClient` çalışıyor (Polly retry ile) | 1 gün |
| **S2** — Domain + Analyzer | `CheapHourAnalyzer`, value object'ler + unit testler | 1 gün |
| **S3** — Use case'ler + MediatR | Use case'ler, custom exception sınıfları | 0.5 gün |
| **S4** — ★ Global Exception Handler | `GlobalExceptionHandler` (`IExceptionHandler`), ProblemDetails, `AddExceptionHandler` + `UseExceptionHandler` | 0.5 gün |
| **S5** — Minimal API endpoint'leri | `/health`, `/trigger/fetch`, `/status` — handler'da try/catch YOK | 0.5 gün |
| **S6** — Bedava bildirim kanalları | Telegram + Gmail SMTP + ntfy.sh + Composite sender | 1 gün |
| **S7** — Serilog + Persistence | Multi-sink Serilog, EF Core + SQLite, idempotency log | 1 gün |
| **S8** — Quartz scheduling + Job bridge | Cron, misfire, job içinden `GlobalExceptionHandler.HandleBackgroundExceptionAsync` | 1 gün |
| **S9** — Integration tests | WireMock.Net ile EPİAŞ taklidi, exception handler testleri | 1 gün |
| **S10** — Docker + CI | Dockerfile, docker-compose, GitHub Actions | 0.5 gün |

**Tahmini toplam:** ~7–8 iş günü (paralel iş aramasıyla ~3 hafta).

---

## 10. Mülakat İçin "Konuşacak Hikaye" Maddeleri

- **Clean Architecture:** *"Saf iş kuralını framework'lerden ayırmak için Clean Architecture seçtim. Domain HttpClient/EF Core/Quartz bilmiyor; provider değiştirmek tek bir adapter güncellemek demek."*
- **★ Global Exception Handling:** *".NET 8'in `IExceptionHandler` interface'ini implement eden tek bir `GlobalExceptionHandler` sınıfım var. Tüm uygulamadaki exception'lar burada switch expression ile karşılanıyor, status code'a maple'leniyor, RFC 7807 ProblemDetails formatında response dönüyor. Use case'lerde ve endpoint'lerde **hiç try/catch yok** — herkes exception serbestçe fırlatıyor, merkez yakalıyor."*
- **Custom Exception sınıfları:** *"`NotFoundException`, `ValidationException`, `EpiasIntegrationException`, `NotificationSendException` gibi anlamlı tipler tanımladım. `GlobalExceptionHandler.MapException()` switch'inde her birine uygun HTTP status code dönüyorum (404, 400, 502, 503)."*
- **Minimal API + BackgroundService hibridi:** *"Worker projesi sadece Quartz job çalıştırmıyor; üzerine ufak bir Minimal API koydum (`/health`, `/trigger/fetch`, `/status`). Bu sayede built-in `IExceptionHandler` middleware'i hem HTTP tarafını hem job tarafını ortak handler ile yönetebiliyor — job içinde catch edilen exception aynı handler'ın paylaşılan mantığına delegate ediliyor."*
- **Kritik hata bildirimi:** *"5xx hataları yakalandığında handler fire-and-forget Telegram'a alarm gönderiyor. Servis düştüğünde Telegram'dan haberim oluyor."*
- **Resilience:** *"EPİAŞ flaky; Polly retry + circuit-breaker + TGT memory cache + idempotency log koydum."*
- **Bedava bildirim seçimi:** *"Türkiye'den çalışan, sıfır maliyetli stack kurdum: Telegram Bot (sınırsız), Gmail SMTP (500/gün), ntfy.sh push (SMS UX'i, kayıt yok). Composite pattern ile paralel gönderim, biri patlasa diğerleri çalışıyor."*
- **CAS/TGT:** *"EPİAŞ CAS protokolünü implement ederken Aralık 2025'teki breaking change'i (query string yerine body) yakaladım ve adapt ettim."*
- **Test stratejisi:** *"Domain pure olduğu için yüzlerce edge-case ms'lerde test edildi. Integration için WireMock.Net ile EPİAŞ taklit ediliyor. `GlobalExceptionHandler` için ayrı testler var — `EpiasIntegrationException` fırlat → 502 + doğru ProblemDetails döner mi diye."*
- **Observability:** *"Serilog ile structured log; her log'da CorrelationId var; bir bildirimin neden gönderildiği baştan sona izlenebilir."*

---

## 11. Genişletme Fikirleri (V2 / V3)

- **Web UI / Blazor dashboard** — Geçmiş fiyat grafikleri, hata logları
- **ML.NET ile fiyat tahmini** — Bir hafta önceden ucuz saat tahmini
- **Çoklu kullanıcı + Identity** — Her kullanıcının kendi eşik/kanal ayarı
- **Akıllı priz entegrasyonu** — Home Assistant / MQTT ile cihazları ucuz saatlerde otomatik açma
- **Gerçek tüketim eşleştirme** — "Bu hafta ucuz saatlerde X TL tasarruf ettin" raporu
- **Self-hosted ntfy server** — Docker ile kendi sunucunda → tam veri kontrolü
- **OpenTelemetry** — Distributed tracing (mülakatta artı puan)

---

## 12. Hızlı Başlangıç Komutları

```bash
# Solution oluştur
mkdir EpiasPriceNotifier && cd $_
dotnet new sln

# Projeleri oluştur
dotnet new classlib -n EpiasPriceNotifier.Domain          -o src/EpiasPriceNotifier.Domain
dotnet new classlib -n EpiasPriceNotifier.Application     -o src/EpiasPriceNotifier.Application
dotnet new classlib -n EpiasPriceNotifier.Infrastructure  -o src/EpiasPriceNotifier.Infrastructure
dotnet new web      -n EpiasPriceNotifier.Worker          -o src/EpiasPriceNotifier.Worker
# (Worker projesi minimal API + BackgroundService hibridi olduğu için 'web' template kullanıyoruz)

# Test projeleri
dotnet new xunit -n EpiasPriceNotifier.Domain.UnitTests       -o tests/EpiasPriceNotifier.Domain.UnitTests
dotnet new xunit -n EpiasPriceNotifier.Application.UnitTests  -o tests/EpiasPriceNotifier.Application.UnitTests

# Bağımlılık yönü (önemli!)
dotnet add src/EpiasPriceNotifier.Application/EpiasPriceNotifier.Application.csproj \
           reference src/EpiasPriceNotifier.Domain/EpiasPriceNotifier.Domain.csproj
dotnet add src/EpiasPriceNotifier.Infrastructure/EpiasPriceNotifier.Infrastructure.csproj \
           reference src/EpiasPriceNotifier.Application/EpiasPriceNotifier.Application.csproj
dotnet add src/EpiasPriceNotifier.Worker/EpiasPriceNotifier.Worker.csproj \
           reference src/EpiasPriceNotifier.Application/EpiasPriceNotifier.Application.csproj
dotnet add src/EpiasPriceNotifier.Worker/EpiasPriceNotifier.Worker.csproj \
           reference src/EpiasPriceNotifier.Infrastructure/EpiasPriceNotifier.Infrastructure.csproj

# Önemli NuGet paketleri
cd src/EpiasPriceNotifier.Application
dotnet add package MediatR
dotnet add package FluentValidation
dotnet add package Microsoft.Extensions.Logging.Abstractions

cd ../EpiasPriceNotifier.Infrastructure
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
dotnet add package Microsoft.Extensions.Http.Polly
dotnet add package Telegram.Bot
dotnet add package MailKit
dotnet add package Serilog
dotnet add package Serilog.Sinks.Console
dotnet add package Serilog.Sinks.File
dotnet add package Serilog.Sinks.SQLite
dotnet add package Quartz

cd ../EpiasPriceNotifier.Worker
dotnet add package Serilog.Extensions.Hosting
dotnet add package Serilog.Sinks.OpenTelemetry
dotnet add package Quartz.Extensions.Hosting
# OpenTelemetry / SigNoz
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add package OpenTelemetry.Instrumentation.AspNetCore
dotnet add package OpenTelemetry.Instrumentation.Http
dotnet add package OpenTelemetry.Instrumentation.Runtime

# User secrets
dotnet user-secrets init
dotnet user-secrets set "Epias:Username" "mail@adresim.com"
dotnet user-secrets set "Epias:Password" "PAROLA"
dotnet user-secrets set "Telegram:BotToken" "BOT_TOKEN"
dotnet user-secrets set "Telegram:ChatId" "CHAT_ID"
dotnet user-secrets set "Email:From" "kendim@gmail.com"
dotnet user-secrets set "Email:AppPassword" "GMAIL_APP_PASSWORD"
dotnet user-secrets set "Otel:IngestionKey" "SIGNOZ_INGESTION_KEY"
```

---

## 13. Faydalı Linkler

**EPİAŞ:**
- Şeffaflık ana sayfa: https://seffaflik.epias.com.tr/
- Teknik dokümantasyon (Swagger): https://seffaflik.epias.com.tr/electricity-service/technical/tr/index.html
- Web Servis Kullanım Kılavuzu: https://seffaflik.epias.com.tr/documentation/web-service-user-guide
- CAS TGT değişiklik duyurusu (Kasım 2025): https://www.epias.com.tr/tum-duyurular/cas-uygulamasindaki-ticket-tgt-alma-servisinde-degisiklik
- Referans Python kütüphanesi: https://github.com/Tideseed/eptr2
- Örnek .NET istemci: https://github.com/CedricScottish/EpiasEMClient

**Bildirim Sağlayıcılar:**
- Telegram Bot API: https://core.telegram.org/bots/api
- Telegram.Bot NuGet: https://github.com/TelegramBots/Telegram.Bot
- Gmail App Password: https://myaccount.google.com/apppasswords
- MailKit dokümantasyonu: http://www.mimekit.net/docs/html/Introduction.htm
- ntfy.sh dokümantasyon: https://docs.ntfy.sh/
- ntfy self-host: https://docs.ntfy.sh/install/

**Mimari & Exception Handling:**
- Jason Taylor Clean Architecture template: https://github.com/jasontaylordev/CleanArchitecture
- MediatR Pipeline Behaviors: https://github.com/jbogard/MediatR/wiki/Behaviors
- Serilog dokümantasyon: https://serilog.net/
- Polly resilience: https://github.com/App-vNext/Polly

**Observability:**
- SigNoz ana sayfa: https://signoz.io/
- SigNoz Cloud (free tier): https://signoz.io/teams/
- SigNoz self-host (docker-compose): https://github.com/SigNoz/signoz
- .NET + Serilog + SigNoz pratik rehber: https://signoz.io/blog/opentelemetry-serilog/
- .NET logs with SigNoz: https://signoz.io/blog/opentelemetry-dotnet-logs/
- OpenTelemetry .NET SDK: https://github.com/open-telemetry/opentelemetry-dotnet
- Serilog.Sinks.OpenTelemetry: https://github.com/serilog/serilog-sinks-opentelemetry
