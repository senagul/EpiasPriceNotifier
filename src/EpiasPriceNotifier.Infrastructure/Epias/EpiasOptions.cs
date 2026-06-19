namespace EpiasPriceNotifier.Infrastructure.Epias;

/// <summary>
/// EPİAŞ entegrasyonu için yapılandırma seçenekleri.
/// appsettings.json içindeki "Epias" bölümüne map'lenir,
/// user-secrets içindeki credentials ile override edilir.
///
/// IOptions&lt;EpiasOptions&gt; olarak DI'a register edilir;
/// adapter sınıfları bu nesneyi inject eder.
/// Magic string'lerden kaçınmak için "SectionName" sabit olarak tanımlandı.
/// </summary>
public sealed class EpiasOptions
{
    /// <summary>appsettings.json'daki bölüm adı. Configuration bind ederken kullanılır.</summary>
    public const string SectionName = "Epias";

    /// <summary>EPİAŞ Şeffaflık servis base URL'i. Sonunda slash olmamalı.</summary>
    /// <example>https://seffaflik.epias.com.tr/electricity-service</example>
    public string BaseUrl { get; init; } = "https://seffaflik.epias.com.tr/electricity-service";

    /// <summary>
    /// CAS (Central Authentication Service) TGT endpoint'i.
    /// Form-urlencoded body ile POST atılır, response'da TGT döner.
    /// </summary>
    public string CasUrl { get; init; } = "https://giris.epias.com.tr/cas/v1/tickets";

    /// <summary>EPİAŞ kayıt e-postası (user-secrets'tan gelir, repo'da boş).</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>EPİAŞ şifresi (user-secrets'tan gelir, repo'da boş).</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// TGT cache süresi (dakika). EPİAŞ TGT'leri 2 saat geçerli;
    /// güvenli tarafta kalmak için 100 dakikada bir yeniliyoruz —
    /// sona yaklaşıp 401 yememek için.
    /// </summary>
    public int TgtCacheMinutes { get; init; } = 100;

    /// <summary>HTTP request timeout (saniye). EPİAŞ bazen yavaş cevap verir.</summary>
    public int HttpTimeoutSeconds { get; init; } = 30;
}