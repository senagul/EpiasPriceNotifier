using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;
using EpiasPriceNotifier.Infrastructure;
using EpiasPriceNotifier.Infrastructure.Notifications;
using EpiasPriceNotifier.Worker.ExceptionHandlers;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

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