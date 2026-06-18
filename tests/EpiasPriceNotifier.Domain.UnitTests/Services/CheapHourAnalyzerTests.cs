using EpiasPriceNotifier.Domain.Entities;
using EpiasPriceNotifier.Domain.Services;
using EpiasPriceNotifier.Domain.ValueObjects;
using FluentAssertions;

namespace EpiasPriceNotifier.Domain.UnitTests.Services;

/// <summary>
/// CheapHourAnalyzer için unit testler.
///
/// Test isimlendirme konvansiyonu: MethodName_WhenCondition_ExpectedResult.
/// Test patladığında ismi okuyan kişi neyin beklendiğini anlar — debug süresi azalır.
///
/// AAA pattern:
/// - Arrange: test verisini hazırla
/// - Act: test edilen method'u çağır (tek satır)
/// - Assert: sonucu doğrula (FluentAssertions ile)
/// </summary>
public class CheapHourAnalyzerTests
{
    // _sut = "System Under Test". Bu konvansiyon olduğu için test'i okuyan
    // hemen "test ettiği şey bu" diyebiliyor. Her testte yeni instance almaktansa
    // field olarak tuttuk çünkü CheapHourAnalyzer stateless.
    private readonly CheapHourAnalyzer _sut = new();

    [Fact]
    public void FindCheapWindows_WhenNoHourBelowThreshold_ReturnsEmpty()
    {
        // Arrange: 24 saat de eşiğin üstünde (1000 TL/MWh = 1 TL/kWh)
        var schedule = BuildSchedule(allHoursPriceTryPerMwh: 1000m);
        var threshold = PriceThreshold.FromTryPerKwh(0.30m); // 0.30 TL/kWh

        // Act
        var windows = _sut.FindCheapWindows(schedule, threshold);

        // Assert: hiçbir saat eşik altı olmadığı için sonuç boş olmalı
        windows.Should().BeEmpty();
    }

    [Fact]
    public void FindCheapWindows_WhenAllHoursBelowThreshold_ReturnsSingleFullDayWindow()
    {
        // Arrange: 24 saat de ucuz (100 TL/MWh = 0.10 TL/kWh)
        var schedule = BuildSchedule(allHoursPriceTryPerMwh: 100m);
        var threshold = PriceThreshold.FromTryPerKwh(0.30m);

        // Act
        var windows = _sut.FindCheapWindows(schedule, threshold);

        // Assert: tek bir 24 saatlik pencere olmalı (algoritmanın "all-cheap" edge case'i)
        windows.Should().HaveCount(1);
        windows[0].HourCount.Should().Be(24);
    }

    [Fact]
    public void FindCheapWindows_GroupsContiguousCheapHours()
    {
        // Arrange: 06:00–15:00 arası ucuz, geri kalanı pahalı
        // (Postman'den gördüğümüz gerçek veri profiline benzer bir senaryo)
        var date = new DateOnly(2026, 6, 18);
        var prices = new List<HourlyPrice>();
        for (var h = 0; h < 24; h++)
        {
            var isCheap = h is >= 6 and <= 15;
            prices.Add(new HourlyPrice(
                new DateTimeOffset(date.ToDateTime(new TimeOnly(h, 0)), TimeSpan.FromHours(3)),
                priceTryPerMwh: isCheap ? 200m : 1000m,
                priceUsdPerMwh: 0m,
                priceEurPerMwh: 0m));
        }
        var schedule = new DailyPriceSchedule(date, prices);
        var threshold = PriceThreshold.FromTryPerKwh(0.30m);

        // Act
        var windows = _sut.FindCheapWindows(schedule, threshold);

        // Assert: 06:00'dan 16:00'a (exclusive) tek bir 10 saatlik pencere
        windows.Should().HaveCount(1);
        windows[0].HourCount.Should().Be(10);
        windows[0].Start.Hour.Should().Be(6);
        windows[0].End.Hour.Should().Be(16); // end exclusive: 15:00 son saat → 16:00 end
    }

    [Fact]
    public void FindCheapWindows_WhenSplitByExpensiveHour_ReturnsTwoSeparateWindows()
    {
        // Arrange: 06–07 ucuz, 08–11 pahalı, 12–14 ucuz tekrar
        // Algoritmanın "ucuz grubu pahalı saatle kapat, yeni grup başlat" mantığını test ediyor
        var date = new DateOnly(2026, 6, 18);
        var prices = new List<HourlyPrice>();
        for (var h = 0; h < 24; h++)
        {
            var isCheap = (h is 6 or 7) || (h is 12 or 13 or 14);
            prices.Add(new HourlyPrice(
                new DateTimeOffset(date.ToDateTime(new TimeOnly(h, 0)), TimeSpan.FromHours(3)),
                priceTryPerMwh: isCheap ? 200m : 1000m,
                priceUsdPerMwh: 0m,
                priceEurPerMwh: 0m));
        }
        var schedule = new DailyPriceSchedule(date, prices);
        var threshold = PriceThreshold.FromTryPerKwh(0.30m);

        // Act
        var windows = _sut.FindCheapWindows(schedule, threshold);

        // Assert: iki ayrı pencere — biri 2 saatlik, biri 3 saatlik
        windows.Should().HaveCount(2);
        windows[0].HourCount.Should().Be(2); // 06:00–08:00
        windows[1].HourCount.Should().Be(3); // 12:00–15:00
    }

    /// <summary>
    /// Test verisi hazırlama yardımcısı. Tüm 24 saatin aynı fiyatta olduğu
    /// bir takvim üretir — "no cheap" ve "all cheap" testleri için pratik.
    ///
    /// Helper kullanmak Arrange kısmını okunaklı tutar. Eğer her testte
    /// 15 satır setup olsaydı, asıl test mantığı kaybolurdu.
    /// </summary>
    private static DailyPriceSchedule BuildSchedule(decimal allHoursPriceTryPerMwh)
    {
        var date = new DateOnly(2026, 6, 18);
        var prices = new List<HourlyPrice>();
        for (var h = 0; h < 24; h++)
        {
            prices.Add(new HourlyPrice(
                // Türkiye saat dilimi (+03:00) — EPİAŞ kaynağıyla tutarlı
                new DateTimeOffset(date.ToDateTime(new TimeOnly(h, 0)), TimeSpan.FromHours(3)),
                priceTryPerMwh: allHoursPriceTryPerMwh,
                priceUsdPerMwh: 0m, // bu testlerde önemli değil
                priceEurPerMwh: 0m));
        }
        return new DailyPriceSchedule(date, prices);
    }
}