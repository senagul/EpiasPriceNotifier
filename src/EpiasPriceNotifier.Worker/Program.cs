using EpiasPriceNotifier.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Infrastructure katmanının tüm servisleri (HttpClient'lar, EpiasOptions binding,
// CasTgtProvider, EpiasPriceClient) tek satırla DI'a kaydedilir.
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// Basit health endpoint. Sağlık kontrolleri ve "service ayakta mı?" testleri için.
// Gerçek iş endpoint'leri (manuel trigger, status, vs.) ilerideki branch'lerde
// MediatR ile gelecek.
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "EpiasPriceNotifier",
    timestamp = DateTime.UtcNow
}));

app.Run();