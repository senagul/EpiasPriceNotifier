using EpiasPriceNotifier.Infrastructure;
using EpiasPriceNotifier.Worker.ExceptionHandlers;

var builder = WebApplication.CreateBuilder(args);

// Infrastructure katmanının tüm servisleri (HttpClient'lar, EpiasOptions binding,
// CasTgtProvider, EpiasPriceClient) tek satırla DI'a kaydedilir.
builder.Services.AddInfrastructure(builder.Configuration);

// ★ Global Exception Handler kayıt (iki satır).
// AddExceptionHandler<T>: T'yi DI'a kaydeder (Singleton lifetime ile gelir).
// AddProblemDetails: ProblemDetails formatlayıcısını DI'a kaydeder.
// İkisi birlikte UseExceptionHandler middleware'inin çalışması için gerekli.
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// ★ Pipeline'a ekle. Pipeline'da MUTLAKA en başa yakın olmalı —
// aşağıdaki endpoint'lerden birinde exception fırlarsa, middleware
// onu yakalayıp GlobalExceptionHandler'a delege eder.
app.UseExceptionHandler();

// Basit health endpoint — service ayakta mı testleri için.
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "EpiasPriceNotifier",
    timestamp = DateTime.UtcNow
}));

// ★ Geçici test endpoint'leri — GlobalExceptionHandler'ı görsel olarak
// doğrulamak için. Bu PR'da commit edip sonraki PR'larda kaldıracağız
// (gerçek endpoint'ler MediatR ile gelince).
app.MapGet("/test/error/notfound", () =>
{
    // Hiç try/catch yok — exception serbestçe fırlar, handler yakalar
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
    // Custom exception değil — bilinmeyen tip default arm'a düşmeli (500)
    throw new InvalidOperationException("Beklenmedik bir şey oldu");
});

app.Run();

// Test projesinin Program'a referans verebilmesi için partial class ipucu.
// WebApplicationFactory<Program> Worker'ı test server olarak ayağa kaldırır.
public partial class Program { }