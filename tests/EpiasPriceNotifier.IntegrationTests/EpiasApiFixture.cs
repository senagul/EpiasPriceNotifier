using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Infrastructure;
using EpiasPriceNotifier.Infrastructure.Epias;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WireMock.Server;

namespace EpiasPriceNotifier.IntegrationTests;

/// <summary>
/// EPİAŞ integration test'leri için ortak fixture.
///
/// Her test sınıfı bunu IClassFixture<T> ile alır → xUnit fixture'ı per-class
/// (sınıf bazında) bir kez kurar, tüm test'ler için paylaşılır. Bu sayede:
///   - WireMock server bir kez ayağa kalkar (port allocation maliyetli)
///   - DI container bir kez kurulur
///   - Her test'te ayrı izole stub'lar tanımlanır
///
/// Diğer fixture seçenekleri:
///   - ICollectionFixture — birden çok sınıfta paylaşmak için
///   - Constructor injection — her test'te yeniden kurulum (yavaş)
/// </summary>
public sealed class EpiasApiFixture : IDisposable
{
    /// <summary>WireMock sahte server. Test'ler buna stub tanımlar.</summary>
    public WireMockServer Server { get; }

    /// <summary>Test edilen IEpiasPriceClient (gerçek EpiasPriceClient, WireMock URL'ine yönlendirilmiş).</summary>
    public IEpiasPriceClient Client { get; }

    private readonly ServiceProvider _provider;

    public EpiasApiFixture()
    {
        // Rastgele port'ta WireMock başlat. Sabit port kullanmamak, CI'da paralel
        // test çalışırken port çakışmasını önler.
        Server = WireMockServer.Start();

        // Test config'i in-memory olarak ayağa kaldır. EpiasOptions'ın URL'lerini
        // WireMock URL'ine yönlendiriyoruz — gerçek EPİAŞ yerine sahte server'a gidecek.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Epias:BaseUrl"] = Server.Url,
                ["Epias:CasUrl"] = $"{Server.Url}/cas/v1/tickets",
                ["Epias:Username"] = "test@example.com",
                ["Epias:Password"] = "test-password",
                ["Epias:TgtCacheMinutes"] = "100",
                ["Epias:HttpTimeoutSeconds"] = "10"
            })
            .Build();

        // Gerçek Infrastructure DI registration'ını kullanıyoruz — production ile
        // birebir aynı kod yolu. Mock yok, sadece HTTP backend değişti (WireMock).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration);

        _provider = services.BuildServiceProvider();
        Client = _provider.GetRequiredService<IEpiasPriceClient>();
    }

    /// <summary>
    /// Tüm WireMock stub'larını ve log'larını sıfırlar.
    /// Her test başında çağrılır → önceki test'in stub'ları bu test'i kirletmez.
    /// </summary>
    public void ResetStubs()
    {
        Server.Reset();
    }

    public void Dispose()
    {
        Server?.Stop();
        Server?.Dispose();
        _provider?.Dispose();
    }
}