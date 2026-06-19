using System.Net;
using System.Text;
using System.Text.Json;
using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Domain.Entities;
using EpiasPriceNotifier.Domain.ValueObjects;
using EpiasPriceNotifier.Infrastructure.Epias.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EpiasPriceNotifier.Infrastructure.Epias;

/// <summary>
/// EPİAŞ'tan günlük PTF (Piyasa Takas Fiyatı) çeken sınıf.
/// IEpiasPriceClient arayüzünü implement eder — Application bunu inject eder,
/// somut tipi bilmek zorunda değildir (Dependency Inversion).
///
/// Akış:
/// 1) CasTgtProvider'dan TGT al (cache hit veya yeni alma)
/// 2) POST /v1/markets/dam/data/mcp endpoint'ine TGT header'ı + body ile çağır
/// 3) 401 dönerse: TGT muhtemelen invalid, cache'i sil ve BİR KEZ daha dene
///    (sonsuz retry yapmıyoruz — bir kez yeter, ikinci kez de patlarsa gerçek sorun var)
/// 4) Response'u McpResponseDto'ya deserialize et
/// 5) DTO'ları Domain'in HourlyPrice'larına çevir, DailyPriceSchedule yarat
///
/// HttpClient IHttpClientFactory tarafından yönetilir — connection pooling,
/// DNS refresh, kullanım sonrası dispose hepsi factory'nin sorumluluğunda.
/// </summary>
public sealed class EpiasPriceClient : IEpiasPriceClient
{
    // MCP endpoint path. BaseUrl ile birleşince:
    // https://seffaflik.epias.com.tr/electricity-service/v1/markets/dam/data/mcp
    private const string McpEndpointPath = "/v1/markets/dam/data/mcp";

    // JSON serializer ayarları — bir kez yarat, tekrar tekrar kullan.
    // PropertyNameCaseInsensitive=true, EPİAŞ JSON'undaki camelCase'i C# property'lere
    // tolere etmeyi sağlıyor (her halükarda JsonPropertyName attribute'larımız var
    // ama emniyet kemeri).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly CasTgtProvider _tgtProvider;
    private readonly EpiasOptions _options;
    private readonly ILogger<EpiasPriceClient> _logger;

    public EpiasPriceClient(
        HttpClient httpClient,
        CasTgtProvider tgtProvider,
        IOptions<EpiasOptions> options,
        ILogger<EpiasPriceClient> logger)
    {
        _httpClient = httpClient;
        _tgtProvider = tgtProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DailyPriceSchedule> GetDailyPricesAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("EPİAŞ PTF çekiliyor: {Date}", date);

        // İlk deneme — eğer 401 olursa cache'i atıp tek bir kere daha deniyoruz.
        // Yardımcı method'a delege ettik ki retry mantığı sade dursun.
        var response = await SendMcpRequestAsync(date, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("EPİAŞ 401 döndü, TGT invalidate edilip tekrar deneniyor");
            _tgtProvider.Invalidate();

            // Önceki response'u dispose et — handle leak olmasın.
            response.Dispose();

            // Tek kez retry. İkinci 401 = gerçek sorun (yanlış credentials, vs.).
            response = await SendMcpRequestAsync(date, cancellationToken);
        }

        // EnsureSuccessStatusCode yerine manuel check yapıyoruz çünkü
        // hata response'unun body'sini log'a yazmak istiyoruz (sadece status code yetmez).
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            _logger.LogError(
                "EPİAŞ MCP başarısız. Status: {Status}, Body: {Body}",
                (int)response.StatusCode, errorBody);
            throw new Exception(
                $"EPİAŞ PTF alınamadı. HTTP {(int)response.StatusCode}: {errorBody}");
        }

        // ReadAsStream + DeserializeAsync = string'e hiç çevirmeden direkt parse.
        // Büyük JSON'larda allocation azaltır (mikro-optimizasyon ama bedavaya geliyor).
        using (response)
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var dto = await JsonSerializer.DeserializeAsync<McpResponseDto>(
                stream, JsonOptions, cancellationToken);

            if (dto is null)
                throw new Exception("EPİAŞ MCP response deserialize edilemedi (null döndü)");

            if (dto.Items.Count == 0)
                throw new Exception($"EPİAŞ MCP boş items döndü. Tarih: {date}");

            _logger.LogInformation(
                "EPİAŞ PTF alındı: {Count} saat, {Date}",
                dto.Items.Count, date);

            // DTO → Domain çevirisi. Mapper'ı ayrı method'a aldık ki
            // bu method odaklı kalsın.
            return MapToDomain(date, dto);
        }
    }

    /// <summary>
    /// MCP endpoint'ine TGT header'ı + body ile POST atar.
    /// HttpResponseMessage'ı çağıran dispose etmekle yükümlüdür (using ile).
    /// Retry mantığı dışarıda olduğu için burada sadece tek bir HTTP turu.
    /// </summary>
    private async Task<HttpResponseMessage> SendMcpRequestAsync(
        DateOnly date, CancellationToken cancellationToken)
    {
        var tgt = await _tgtProvider.GetTgtAsync(cancellationToken);

        // Body'i DTO üzerinden serialize ediyoruz — string concat ile JSON yazmak hatalı.
        var requestDto = McpRequestDto.ForDate(date);
        var bodyJson = JsonSerializer.Serialize(requestDto, JsonOptions);

        var url = $"{_options.BaseUrl}{McpEndpointPath}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };

        // EPİAŞ'ın beklediği auth header'ı — Bearer DEĞİL, doğrudan "TGT" adında bir header.
        // Bu standart bir auth pattern değil, EPİAŞ'a özgü.
        request.Headers.Add("TGT", tgt);
        request.Headers.Accept.ParseAdd("application/json");

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// EPİAŞ ham DTO'sundan Domain aggregate root'una dönüşüm.
    /// Bu method'un yaptığı tek şey: birim/format çevirisi + Domain constructor'larını çağırmak.
    /// İş kuralı buraya gelmemeli — burası saf bir mapper.
    /// </summary>
    private static DailyPriceSchedule MapToDomain(DateOnly date, McpResponseDto dto)
    {
        // Her item için Domain HourlyPrice yarat. Constructor invariant'ları
        // (negatif fiyat, tam saat) burada otomatik kontrol edilir — EPİAŞ
        // garip bir veri yollasa Domain reddeder.
        var hourlyPrices = dto.Items
            .Select(item => new HourlyPrice(
                hour: item.Date,
                priceTryPerMwh: item.Price,
                priceUsdPerMwh: item.PriceUsd,
                priceEurPerMwh: item.PriceEur))
            .ToList();

        // DailyPriceSchedule constructor'ı 3 invariant'ı kontrol ediyor:
        // 24 saat olmalı, hepsi aynı gün olmalı, tekil saatler olmalı.
        // EPİAŞ bug'lı veri yollarsa burada exception fırlar — fail fast.
        return new DailyPriceSchedule(date, hourlyPrices);
    }
}