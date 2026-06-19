using System.Text.Json.Serialization;

namespace EpiasPriceNotifier.Infrastructure.Epias.Dtos;

/// <summary>
/// EPİAŞ'ın /v1/markets/dam/data/mcp endpoint'inden dönen ham response.
/// Bu sınıf bilinçli olarak "anemic" — sadece JSON deserialize için kullanılır,
/// iş kuralı içermez. Mapper, bunu DailyPriceSchedule domain object'ine çevirir.
///
/// Postman'de gözlemlediğimiz JSON şemasını birebir yansıtır:
/// { "items": [...], "page": null, "statistic": {...} }
/// </summary>
internal sealed class McpResponseDto
{
    /// <summary>24 saatlik fiyat dizisi. Her item bir saatin PTF'i.</summary>
    [JsonPropertyName("items")]
    public List<McpItemDto> Items { get; set; } = new();

    /// <summary>
    /// EPİAŞ tarafından eklenen ön-hesaplanmış istatistikler.
    /// Şimdilik kullanmıyoruz (domain'de kendi hesaplıyoruz), ama opsiyonel referans.
    /// </summary>
    [JsonPropertyName("statistic")]
    public McpStatisticDto? Statistic { get; set; }
}

/// <summary>EPİAŞ'tan gelen tek saatlik fiyat satırı.</summary>
internal sealed class McpItemDto
{
    /// <summary>Saatin tam timestamp'i (ISO 8601, +03:00 offset ile).</summary>
    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }

    /// <summary>"HH:mm" formatında saat string'i — şu an kullanmıyoruz ama deserializer atlamak için tutuyoruz.</summary>
    [JsonPropertyName("hour")]
    public string? Hour { get; set; }

    /// <summary>TL/MWh cinsinden fiyat.</summary>
    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("priceUsd")]
    public decimal PriceUsd { get; set; }

    [JsonPropertyName("priceEur")]
    public decimal PriceEur { get; set; }
}

/// <summary>EPİAŞ'ın hediye paketi — domain'de tekrar hesaplıyoruz ama referans için map'liyoruz.</summary>
internal sealed class McpStatisticDto
{
    [JsonPropertyName("priceAvg")]
    public decimal PriceAvg { get; set; }

    [JsonPropertyName("priceMin")]
    public decimal PriceMin { get; set; }

    [JsonPropertyName("priceMax")]
    public decimal PriceMax { get; set; }
}