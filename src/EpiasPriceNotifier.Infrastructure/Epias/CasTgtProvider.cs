using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EpiasPriceNotifier.Infrastructure.Epias;

/// <summary>
/// EPİAŞ CAS (Central Authentication Service) üzerinden TGT
/// (Ticket Granting Ticket) yönetir.
///
/// Sorumluluklar:
/// - Username/password ile POST /cas/v1/tickets çağırıp TGT alır
/// - TGT'yi memory cache'e koyar (~100 dk, gerçek geçerlik 120 dk)
/// - Concurrent istekler için lock koyar (SemaphoreSlim) — aynı anda
///   100 thread "TGT lazım" derse sadece 1 HTTP çağrısı yapılır
/// - InvalidateAsync ile stale TGT'leri zorla atabilir (401 alındığında çağrılır)
///
/// Singleton lifetime ile DI'a register edilir — cache state korunsun diye.
///
/// Önemli (Kasım 2025 breaking change): username/password QUERY STRING yerine
/// BODY içinde (form-urlencoded) gönderilmek zorunda. Eski örnekler bunu
/// query'ye koyuyor — biz body'e koyuyoruz.
/// </summary>
public sealed class CasTgtProvider
{
    // Cache key sabit — tek TGT tutuyoruz çünkü tek bir EPİAŞ hesabı var.
    // Çoklu hesaba çıkarsak key'i kullanıcı adı yapardık.
    private const string CacheKey = "EPIAS_TGT";

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly EpiasOptions _options;
    private readonly ILogger<CasTgtProvider> _logger;

    // SemaphoreSlim(1, 1) → mutex (semaphore with max 1 concurrent holder).
    // İki nedenle SemaphoreSlim seçildi (lock keyword'üne tercih):
    //   1) await'lenebilir (lock async kodda kullanılamaz)
    //   2) daha az allocation, modern API
    private readonly SemaphoreSlim _refreshLock = new(initialCount: 1, maxCount: 1);

    public CasTgtProvider(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<EpiasOptions> options,
        ILogger<CasTgtProvider> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Geçerli bir TGT döndürür. Cache'de varsa onu, yoksa yeni alıp cache'leyip döner.
    /// Thread-safe: aynı anda 100 çağrı gelse bile EPİAŞ'a sadece 1 HTTP atılır.
    /// </summary>
    public async Task<string> GetTgtAsync(CancellationToken cancellationToken = default)
    {
        // 1. Hızlı yol: cache'de varsa hiç lock'a girme.
        // Çoğu çağrı bu satırda biter — sıcak yolu (hot path) hızlı tutuyoruz.
        if (_cache.TryGetValue<string>(CacheKey, out var cachedTgt) && !string.IsNullOrEmpty(cachedTgt))
        {
            return cachedTgt;
        }

        // 2. Yavaş yol: cache miss. Lock'a girip TGT alacağız.
        // Burada birden çok thread aynı anda girebilir, semaphore sıraya dizer.
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check pattern: lock'a girdik, AMA bekledik diğeri zaten
            // taze TGT yazmış olabilir. Tekrar bakmadan boşa çağrı atmayalım.
            if (_cache.TryGetValue<string>(CacheKey, out var freshlyCached) && !string.IsNullOrEmpty(freshlyCached))
            {
                return freshlyCached;
            }

            // 3. Gerçekten almamız gerekiyor — EPİAŞ CAS'e çağrı atalım.
            var newTgt = await FetchNewTgtAsync(cancellationToken);
            CacheTgt(newTgt);
            return newTgt;
        }
        finally
        {
            // Lock'u her durumda bırak (exception olsa bile) — deadlock önleme.
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Cache'deki TGT'yi atar. EpiasPriceClient 401 alınca çağırır,
    /// bir sonraki GetTgtAsync çağrısı yeni TGT alır.
    /// </summary>
    public void Invalidate()
    {
        _cache.Remove(CacheKey);
        _logger.LogInformation("EPİAŞ TGT cache invalidate edildi");
    }

    /// <summary>
    /// EPİAŞ CAS'e POST /cas/v1/tickets çağrısı yapar, TGT döner.
    /// Form-urlencoded body kullanmak ZORUNLU (Aralık 2025 breaking change).
    /// </summary>
    private async Task<string> FetchNewTgtAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("EPİAŞ CAS'ten yeni TGT alınıyor: {CasUrl}", _options.CasUrl);

        // Body: application/x-www-form-urlencoded.
        // FormUrlEncodedContent kendisi Content-Type header'ını otomatik set ediyor.
        var formBody = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", _options.Username),
            new KeyValuePair<string, string>("password", _options.Password)
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.CasUrl)
        {
            Content = formBody
        };
        // EPİAŞ TGT'yi text/plain olarak döndürüyor, bunu kabul ediyoruz.
        request.Headers.Accept.ParseAdd("text/plain");

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        // Başarısızlıkta detaylı log + exception. Şu an generic Exception fırlatıyoruz;
        // ileride GlobalExceptionHandler bölümünde EpiasIntegrationException ekleyeceğiz.
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "EPİAŞ CAS başarısız. Status: {Status}, Body: {Body}",
                (int)response.StatusCode, errorBody);
            throw new Exception(
                $"EPİAŞ TGT alınamadı. HTTP {(int)response.StatusCode}: {errorBody}");
        }

        var tgt = await response.Content.ReadAsStringAsync(cancellationToken);

        // Sanity check: TGT-XXX formatında bir string bekliyoruz.
        // EPİAŞ bazen Location header'ında dönderiyor — body boşsa oradan dene.
        if (string.IsNullOrWhiteSpace(tgt))
        {
            if (response.Headers.Location != null)
            {
                // Location: .../cas/v1/tickets/TGT-237-U0TU... → sonu al
                tgt = response.Headers.Location.Segments[^1];
            }
        }

        if (string.IsNullOrWhiteSpace(tgt) || !tgt.StartsWith("TGT-"))
        {
            _logger.LogError("EPİAŞ TGT response beklenen formatta değil: '{Tgt}'", tgt);
            throw new Exception($"TGT formatı geçersiz: '{tgt}'");
        }

        _logger.LogInformation("EPİAŞ TGT alındı (cache'leniyor, TTL: {Ttl} dk)",
            _options.TgtCacheMinutes);
        return tgt.Trim();
    }

    /// <summary>TGT'yi memory cache'e koyar, TTL ile.</summary>
    private void CacheTgt(string tgt)
    {
        var cacheOptions = new MemoryCacheEntryOptions
        {
            // Absolute expiration — TGT geçerlik süresine bağlı, mutlak süre.
            // Sliding expiration KULLANMAYIN: TGT 2 saatte ölecek, ne kadar
            // kullanılırsa kullanılsın o süre dolduğunda invalid olacak.
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_options.TgtCacheMinutes)
        };
        _cache.Set(CacheKey, tgt, cacheOptions);
    }
}