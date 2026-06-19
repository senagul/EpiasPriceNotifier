using System.Text.Json.Serialization;

namespace EpiasPriceNotifier.Infrastructure.Epias.Dtos;

/// <summary>
/// EPİAŞ /v1/markets/dam/data/mcp endpoint'ine gönderilen request body.
/// Format Postman'de doğrulandı:
/// { "startDate": "2026-06-18T00:00:00+03:00", "endDate": "2026-06-18T00:00:00+03:00" }
///
/// startDate ve endDate aynı gün verilince o güne ait 24 saat dönüyor.
/// </summary>
internal sealed class McpRequestDto
{
    [JsonPropertyName("startDate")]
    public string StartDate { get; set; } = string.Empty;

    [JsonPropertyName("endDate")]
    public string EndDate { get; set; } = string.Empty;

    /// <summary>
    /// Verilen tarih için request body oluşturur.
    /// EPİAŞ ISO 8601 format ister + Türkiye saat dilimi (+03:00) offset'i.
    /// </summary>
    public static McpRequestDto ForDate(DateOnly date)
    {
        // EPİAŞ'ın beklediği tam format: 2026-06-18T00:00:00+03:00
        var iso = $"{date:yyyy-MM-dd}T00:00:00+03:00";
        return new McpRequestDto { StartDate = iso, EndDate = iso };
    }
}