using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Infrastructure.Epias;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EpiasPriceNotifier.Infrastructure;

/// <summary>
/// Infrastructure katmanının tüm servislerini DI container'a kaydeder.
///
/// Bu pattern (her katmanda bir DI extension method) Clean Architecture
/// projelerinde standarttır — composition root (Worker/Program.cs) küçük
/// kalır, katman içsel detaylarına bulaşmaz. Yarın yeni bir Infrastructure
/// servisi eklersen Worker'a dokunmana gerek yok, sadece bu method'a ekliyorsun.
///
/// Worker/Program.cs içinden çağrımı:
///   builder.Services.AddInfrastructure(builder.Configuration);
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── 1) Configuration binding ─────────────────────────────────
        // EpiasOptions'ı appsettings.json'daki "Epias" bölümüne bind et.
        // .Bind() bir kez bind eder; daha güçlü yol .Configure<T>():
        services
            .AddOptions<EpiasOptions>()
            .Bind(configuration.GetSection(EpiasOptions.SectionName))
            // ValidateDataAnnotations + ValidateOnStart kombosu, eksik config'i
            // uygulama daha çalışmaya başlamadan yakalar — fail fast.
            // Şimdilik DataAnnotation eklemedik, ama altyapısı duruyor.
            .ValidateOnStart();

        // ── 2) Memory Cache (TGT cache için lazım) ───────────────────
        // IMemoryCache, CasTgtProvider'a inject olacak. Singleton ile gelir.
        services.AddMemoryCache();

        // ── 3) CasTgtProvider için HttpClient ────────────────────────
        // AddHttpClient<T> üç şey birden yapar:
        //   - T'yi DI'a kaydeder (Transient default'la, ama HttpClient için sorun değil)
        //   - HttpClient'i factory'den alıp T'nin constructor'ına inject eder
        //   - Connection pooling, DNS refresh, dispose yönetimini factory'ye bırakır
        services.AddHttpClient<CasTgtProvider>((sp, client) =>
        {
            // sp.GetRequiredService<IOptions<EpiasOptions>>() ile config'i okuyup
            // HttpClient timeout'unu config'den alıyoruz. Magic number yok.
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EpiasOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
        });

        // ── 4) EpiasPriceClient için HttpClient ──────────────────────
        // Aynı pattern. Bu HttpClient ayrı bir handler pool kullanır — CAS ile
        // MCP farklı host'lar (giris.epias.com.tr vs seffaflik.epias.com.tr),
        // ayrı HttpClient ile DNS lookup'larını da ayrı yapıyoruz.
        //
        // EpiasPriceClient, IEpiasPriceClient arayüzüyle DI'a giriyor —
        // Application katmanı bu arayüz üzerinden inject ediyor, somut sınıfa
        // bağımlı değil. Bu Dependency Inversion'ın somut uygulaması.
        services.AddHttpClient<IEpiasPriceClient, EpiasPriceClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EpiasOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
        });

        return services;
    }
}