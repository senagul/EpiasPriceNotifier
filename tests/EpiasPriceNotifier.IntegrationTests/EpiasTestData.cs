using System.Globalization;
using System.Text;

namespace EpiasPriceNotifier.IntegrationTests;

/// <summary>
/// Test'lerde EPİAŞ-uyumlu JSON yanıtları üreten yardımcı.
/// Test sınıflarının "arrange" bloklarını okunabilir tutar — herkesin
/// JSON string'lerini elle yazmak zorunda kalmaması için.
/// </summary>
internal static class EpiasTestData
{
    /// <summary>
    /// EPİAŞ MCP endpoint'inin döndürdüğü formatta JSON üretir.
    /// </summary>
    /// <param name="date">PTF tarihi (Türkiye saat dilimi varsayılıyor).</param>
    /// <param name="hourlyPrices">
    /// 24 elemanlı dizi: index = saat (0..23), değer = TL/MWh fiyat.
    /// Eksik vermek istersen testte özellikle invalid senaryo kuruyorsundur.
    /// </param>
    public static string McpResponseJson(DateOnly date, decimal[] hourlyPrices)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"items\": [");

        for (var h = 0; h < hourlyPrices.Length; h++)
        {
            if (h > 0) sb.Append(',');
            // Decimal değeri culture-independent yazıyoruz (Turkish culture'da
            // 250,50 olur, EPİAŞ JSON ise 250.50 bekliyor).
            var priceStr = hourlyPrices[h].ToString(CultureInfo.InvariantCulture);
            sb.Append($$"""
                {
                    "date": "{{date:yyyy-MM-dd}}T{{h:D2}}:00:00+03:00",
                    "hour": "{{h:D2}}:00",
                    "price": {{priceStr}},
                    "priceUsd": 0,
                    "priceEur": 0
                }
                """);
        }

        sb.Append("], \"page\": null, \"statistic\": null }");
        return sb.ToString();
    }

    /// <summary>Tüm saatleri aynı fiyatta üretir — basit test senaryoları için.</summary>
    public static string McpResponseJsonUniform(DateOnly date, decimal pricePerMwh)
    {
        var prices = new decimal[24];
        Array.Fill(prices, pricePerMwh);
        return McpResponseJson(date, prices);
    }

    /// <summary>EPİAŞ tarafından dönen geçerli formatta sahte TGT string'i.</summary>
    public const string FakeTgt = "TGT-237-FAKE-TGT-FOR-TESTING-cas01.epias.com.tr";
}