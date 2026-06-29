using EpiasPriceNotifier.Application;
using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;
using EpiasPriceNotifier.Infrastructure;
using EpiasPriceNotifier.Infrastructure.Notifications;
using EpiasPriceNotifier.Infrastructure.Persistence;
using EpiasPriceNotifier.Worker.ExceptionHandlers;
using EpiasPriceNotifier.Worker.Jobs;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Quartz;
using Serilog;
using SchedulingOptions = EpiasPriceNotifier.Worker.Jobs.SchedulingOptions;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using OpenTelemetry.Exporter;

// Bootstrap logger — Serilog DI'a girmeden önceki erken hataları yakalar.
// Container ayağa kalkamasa bile bu seviyede log üretebilir.
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// Quartz çok fazla log çıkarır ("Job triggered", "Scheduler going to sleep", vs.)
// Information seviyesinde tutmak uygulama logunu kirletir; Warning yeterli.
builder.Logging.AddFilter("Quartz", LogLevel.Warning);

// ★ Quartz scheduler kaydı
// QuartzOptions'ı config'ten bind ediyoruz; FetchAndNotifyJob'ı Quartz'a
// kaydedip cron tetiklemesi tanımlıyoruz.
builder.Services.Configure<SchedulingOptions>(
    builder.Configuration.GetSection(SchedulingOptions.SectionName));

builder.Services.AddQuartz(q =>
{
    var schedulingOpts = builder.Configuration
        .GetSection(SchedulingOptions.SectionName)
        .Get<SchedulingOptions>() ?? new SchedulingOptions();


    var timeZone = TimeZoneInfo.FindSystemTimeZoneById(schedulingOpts.TimeZone);

    var jobKey = new JobKey("FetchAndNotifyJob");
    q.AddJob<FetchAndNotifyJob>(opts => opts.WithIdentity(jobKey));

    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity("FetchAndNotifyJob-trigger")
        .WithCronSchedule(schedulingOpts.FetchAndNotifyCron, x => x
            .InTimeZone(timeZone)
            .WithMisfireHandlingInstructionDoNothing()));

});

// Quartz'ı IHostedService olarak host'a tak — uygulama başlayınca
// scheduler ayağa kalkar, kapanırken graceful shutdown.
// WaitForJobsToComplete = true: shutdown sırasında çalışan job'ları bekle.
builder.Services.AddQuartzHostedService(opts =>
{
    opts.WaitForJobsToComplete = true;
});

// Application katmanı — MediatR + handler'lar
builder.Services.AddApplication();

// Infrastructure katmanının tüm servisleri (EPİAŞ + Bildirimler) tek satırla
builder.Services.AddInfrastructure(builder.Configuration);

// Global Exception Handler
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Serilog: structured logging + OpenTelemetry sink.
// Configuration'dan okuyor — appsettings.json'a Serilog bölümü ekleyeceğiz.
// Default minimum level Information; Microsoft.AspNetCore Warning'e çekildi.
builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "EpiasPriceNotifier")
    .WriteTo.Console()
    .WriteTo.OpenTelemetry(opts =>
    {
        opts.Endpoint = context.Configuration["OpenTelemetry:Endpoint"] ?? "http://localhost:4317";
        opts.Protocol = Serilog.Sinks.OpenTelemetry.OtlpProtocol.Grpc;
        opts.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = "EpiasPriceNotifier", ["service.version"] = "1.0.0", ["deployment.environment"] = context.HostingEnvironment.EnvironmentName };
    }));


// Health checks — liveness (process responsive) ve readiness (dependencies ready) ayrı endpoint'lerde.
// AddSqlite SQLite connection açıp kapatıyor, gerçek bağlantı problemi varsa Unhealthy döner.
builder.Services.AddHealthChecks()
    .AddSqlite(connectionString: builder.Configuration.GetConnectionString("Default") ?? "Data Source=epias-price-notifier.db", name: "sqlite", tags: new[] { "ready" });

// ─── OpenTelemetry: Tracing + Metrics ──────────────────────────────────────
// Logs zaten Serilog -> OpenTelemetry sink üzerinden gidiyor.
// Burada distributed tracing ve metrics export ediyoruz aynı endpoint'e.
//
// service.name = "EpiasPriceNotifier" (SigNoz UI'da Services sekmesinde böyle görünecek).
// AspNetCore + HttpClient + EF Core otomatik instrumentation:
//   - HTTP request'leri otomatik trace
//   - Outbound HttpClient çağrıları (EPİAŞ, Telegram, ntfy) otomatik trace
//   - EF Core sorguları otomatik trace
// Hiç manuel kod yazmamıza gerek yok — sadece "AddXxxInstrumentation()" çağırıyoruz.
var otelEndpoint = builder.Configuration["OpenTelemetry:Endpoint"] ?? "http://localhost:4317";
var serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "EpiasPriceNotifier";
var serviceVersion = builder.Configuration["OpenTelemetry:ServiceVersion"] ?? "1.0.0";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName: serviceName, serviceVersion: serviceVersion).AddAttributes(new[] { new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName) }))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("EpiasPriceNotifier.*")
        .AddOtlpExporter(opts => { opts.Endpoint = new Uri(otelEndpoint); opts.Protocol = OtlpExportProtocol.Grpc; }))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter("EpiasPriceNotifier.*")
        .AddOtlpExporter(opts => { opts.Endpoint = new Uri(otelEndpoint); opts.Protocol = OtlpExportProtocol.Grpc; }));

var app = builder.Build();

// ★ Database Initialization
// Uygulama başlamadan önce DB şemasını migration'larla senkronize et.
// İlk çalıştırmada DB dosyası yoksa yaratır + tüm tabloları kurar.
// Sonraki çalıştırmalarda yeni migration varsa apply eder.
//
// Niye builder.Build() sonrasında ve UseExceptionHandler öncesinde?
// DI container hazır (Build sonrası), middleware pipeline'a girmedik (route
// kayıtları öncesi). DB ayağa kalkmazsa uygulamanın hiç başlamaması iyi —
// fail-fast.
//
// Niye EnsureCreated değil Migrate?
// EnsureCreated migration history'sini takip etmiyor — yarın yeni migration
// eklersek tabloyu modify edemiyor. Migrate ise tüm migration'ları sırayla
// uyguluyor, mevcut DB'yi schema'ya getiriyor. Production-grade yol.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.UseExceptionHandler();

// Health endpoints — Docker/Kubernetes probes.
// Liveness: sadece process responsive mi. Hiç dependency check etmez.
// Readiness: "ready" tag'li tüm check'leri çalıştırır (şu an SQLite).
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false // hiç check çalıştırma, sadece "endpoint var mı" sorusu
});

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"), // "ready" tag'li tüm check'leri çalıştır
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var response = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description, duration = e.Value.Duration.TotalMilliseconds }),
            totalDuration = report.TotalDuration.TotalMilliseconds
        };
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(response));
    }
});


// Exception handler test endpoint'leri (önceki PR'dan)
app.MapGet("/test/error/notfound", () =>
{
    throw new EpiasPriceNotifier.Application.Common.Exceptions.NotFoundException(
        entityName: "DailyPriceSchedule",
        key: "2026-06-99");
});

app.MapGet("/test/error/validation", () =>
{
    throw new EpiasPriceNotifier.Application.Common.Exceptions.ValidationException(
        new Dictionary<string, string[]>
        {
            ["Date"] = new[] { "Geçerli bir tarih girin (yyyy-MM-dd)" },
            ["Threshold"] = new[] { "Eşik 0'dan büyük olmalı" }
        });
});

app.MapGet("/test/error/epias", () =>
{
    throw new EpiasPriceNotifier.Application.Common.Exceptions.EpiasIntegrationException(
        message: "EPİAŞ timeout",
        statusCode: 504);
});

app.MapGet("/test/error/unhandled", () =>
{
    throw new InvalidOperationException("Beklenmedik bir şey oldu");
});

#region test/debug-prices endpoint'leri (EPİAŞ'tan gelen ham veriyi görmek için)
// ★ DEBUG: EPİAŞ'tan gelen ham veriyi tarayıcıdan görmek için
// Hatalı saat gösterimini ayıklamak için. Sonra silinecek.
//app.MapGet("/test/debug-prices/{date}", async (
//    string date,
//    IEpiasPriceClient client,
//    CancellationToken ct) =>
//{
//    var d = DateOnly.Parse(date);
//    var schedule = await client.GetDailyPricesAsync(d, ct);

//    return Results.Ok(new
//    {
//        date = schedule.Date,
//        hours = schedule.Hours.Select(h => new
//        {
//            // 3 farklı format — hangisi doğru görünüyor analiz edelim
//            hourFromOffset = h.Hour.ToString("HH:mm"),
//            hourUtc = h.Hour.UtcDateTime.ToString("HH:mm"),
//            hourLocal = h.Hour.LocalDateTime.ToString("HH:mm"),
//            offset = h.Hour.Offset.ToString(),
//            priceTryPerMwh = h.PriceTryPerMwh,
//            priceTryPerKwh = h.PriceTryPerKwh,
//            isFree = h.IsFree
//        })
//    });
//});

#endregion


// ★ GERÇEK İŞ AKIŞINI TEST EDEN ENDPOINT
//
// EPİAŞ'tan gerçek veri çekip ucuz saatleri bulup tüm recipient'lara
// gerçek bildirim atar. /test/notify'dan farkı: sabit "test mesajı"
// yerine analiz edilmiş gerçek içerik gönderiyor.
//
// Tarih query parameter olarak verilebilir, yoksa bugün kullanılır.
//   POST /test/run-cheap-hours
//   POST /test/run-cheap-hours?date=2026-06-18
app.MapPost("/test/run-cheap-hours", async (
    string? date,
    IMediator mediator,
    CancellationToken ct) =>
{
    var targetDate = string.IsNullOrWhiteSpace(date)
        ? DateOnly.FromDateTime(DateTime.Today)
        : DateOnly.Parse(date);

    await mediator.Send(new FetchAndNotifyCheapHoursCommand(targetDate), ct);

    return Results.Ok(new
    {
        triggered = true,
        date = targetDate.ToString("yyyy-MM-dd")
    });
});

// ★ DEBUG: Quartz job'ını manuel tetikle (cron beklemeden test için)
// Production'da kaldırılacak.
app.MapPost("/test/trigger-job", async (
    ISchedulerFactory schedulerFactory,
    CancellationToken ct) =>
{
    var scheduler = await schedulerFactory.GetScheduler(ct);
    var jobKey = new JobKey("FetchAndNotifyJob");

    await scheduler.TriggerJob(jobKey, ct);

    return Results.Ok(new
    {
        triggered = true,
        message = "FetchAndNotifyJob manuel olarak tetiklendi"
    });
});

// ★ GEÇİCİ NOTIFICATION TEST ENDPOINT'İ
//
// Bu endpoint config'ten okuduğu tüm recipient'lara test mesajı yollar.
// Tarayıcıdan veya curl ile çağırırsın:
//   GET http://localhost:5227/test/notify
//
// Asıl bildirim akışı ileride Quartz job'ı + MediatR use case'i üzerinden
// gelecek; bu sadece "altyapı çalışıyor mu" sanity check'i.
app.MapPost("/test/notify", async (
    INotificationDispatcher dispatcher,
    IOptions<NotificationOptions> options,
    CancellationToken ct) =>
{
    var notificationOpts = options.Value;

    // Config'teki Recipients dictionary'sini Domain Recipient nesnelerine çevir
    // Key = isim (User1/User2/User3), Value = ["Telegram", "Email"] string array
    var recipients = notificationOpts.Recipients.Select(kvp =>
    {
        // String enum array'ini NotificationChannel enum array'ine map'le
        // Enum.Parse case-insensitive: "telegram" ve "Telegram" ikisi de çalışır
        var channels = kvp.Value
            .Select(s => Enum.Parse<NotificationChannel>(s, ignoreCase: true))
            .ToList();
        return new Recipient(kvp.Key, channels);
    }).ToList();

    // Gönder
    await dispatcher.SendAsync(
        recipients,
        subject: "EpiasPriceNotifier — Test Bildirim",
        body: $"Bu bir test mesajıdır. {DateTime.Now:dd MMMM yyyy HH:mm} itibariyle altyapı çalışıyor ⚡",
        cancellationToken: ct);

    return Results.Ok(new
    {
        sent = true,
        recipientCount = recipients.Count,
        recipients = recipients.Select(r => new
        {
            name = r.Name,
            channels = r.Channels.Select(c => c.ToString())
        })
    });
});

app.Run();

// Test projesinin Program'a referans verebilmesi için partial class ipucu.
// WebApplicationFactory<Program> Worker'ı test server olarak ayağa kaldırır.
public partial class Program { }