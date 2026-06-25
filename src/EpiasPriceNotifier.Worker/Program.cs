using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;
using EpiasPriceNotifier.Infrastructure;
using EpiasPriceNotifier.Infrastructure.Notifications;
using EpiasPriceNotifier.Worker.ExceptionHandlers;
using Microsoft.Extensions.Options;
using EpiasPriceNotifier.Application;
using EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;
using MediatR;

var builder = WebApplication.CreateBuilder(args);

// Application katmanı — MediatR + handler'lar
builder.Services.AddApplication();

// Infrastructure katmanının tüm servisleri (EPİAŞ + Bildirimler) tek satırla
builder.Services.AddInfrastructure(builder.Configuration);

// Global Exception Handler
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

// Basit health endpoint
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "EpiasPriceNotifier",
    timestamp = DateTime.UtcNow
}));

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