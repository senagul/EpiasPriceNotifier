using EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;
using EpiasPriceNotifier.Domain.Entities;
using EpiasPriceNotifier.Domain.Services;
using EpiasPriceNotifier.Domain.ValueObjects;
using FluentAssertions;

namespace EpiasPriceNotifier.Application.UnitTests.UseCases.FetchAndNotifyCheapHours;

/// <summary>
/// CheapHoursMessageFormatter'ın çıktı formatını test eder.
///
/// Test stratejisi: tam string match yerine "bölüm başlığı var mı", "doğru
/// pencere bilgisi var mı", "regresyon bug'ları geri gelmiş mi" davranışsal
/// kontrol. Format ufak ayarlarda kırılgan olmaz.
///
/// Önemli regression test: 21 Haziran 2026 bug'ı — bedava ve pozitif-ucuz
/// saatlerin AYRI bölümlerde olduğunu garanti eder.
/// </summary>
public class CheapHoursMessageFormatterTests
{
    private static readonly PriceThreshold Threshold = PriceThreshold.FromTryPerKwh(0.30m);
    private static readonly DateOnly TestDate = new(2026, 6, 21);

    [Fact]
    public void Format_WithNoFreeOrCheapHours_ShowsBothSectionsEmpty()
    {
        var schedule = BuildSchedule(Enumerable.Repeat(1000m, 24).ToArray());
        var emptyWindows = Array.Empty<CheapWindow>();

        var (subject, body) = CheapHoursMessageFormatter.Format(schedule, emptyWindows, Threshold);

        subject.Should().Contain("Fiyat Raporu");
        body.Should().Contain("BEDAVA SAATLER");
        body.Should().Contain("UCUZ SAATLER");
        var occurrences = body.Split("(Bugün Yok)").Length - 1;
        occurrences.Should().Be(2);
    }

    [Fact]
    public void Format_WithOnlyFreeHours_ListsFreeWindowAndEmptyCheapSection()
    {
        var prices = new decimal[24];
        for (var h = 0; h < 24; h++)
            prices[h] = (h >= 7 && h <= 16) ? 0m : 1000m;

        var schedule = BuildSchedule(prices);
        var cheapWindows = new[] { BuildWindow(schedule, startHour: 7, endHour: 17) };

        var (subject, body) = CheapHoursMessageFormatter.Format(schedule, cheapWindows, Threshold);

        subject.Should().Contain("BEDAVA");
        body.Should().Contain("07:00");
        body.Should().Contain("17:00");
        body.Should().Contain("(10 saat)");

        var cheapSectionIdx = body.IndexOf("UCUZ SAATLER", StringComparison.Ordinal);
        var cheapSection = body.Substring(cheapSectionIdx);
        cheapSection.Should().Contain("(Bugün Yok)");
    }

    [Fact]
    public void Format_WithBothFreeAndPositiveCheapHours_SeparatesIntoTwoSections()
    {
        var prices = new decimal[24];
        for (var h = 0; h < 24; h++)
        {
            prices[h] = h switch
            {
                5 => 237m,
                6 => 171m,
                >= 7 and <= 16 => 0m,
                17 => 171m,
                _ => 1500m
            };
        }

        var schedule = BuildSchedule(prices);
        var analyzer = new CheapHourAnalyzer();
        var cheapWindows = analyzer.FindCheapWindows(schedule, Threshold);

        var (subject, body) = CheapHoursMessageFormatter.Format(schedule, cheapWindows, Threshold);

        body.Should().Contain("07:00");
        body.Should().Contain("17:00");
        body.Should().Contain("(10 saat)");

        body.Should().NotContain("13 saat");

        body.Should().Contain("05:00");
        body.Should().Contain("18:00");

        subject.Should().Contain("BEDAVA");
    }

    [Fact]
    public void Format_AlwaysShowsDailySummaryWithCheapestExpensiveAndAverage()
    {
        var prices = new decimal[24];
        for (var h = 0; h < 24; h++) prices[h] = (h + 1) * 100m;

        var schedule = BuildSchedule(prices);

        var (_, body) = CheapHoursMessageFormatter.Format(schedule, Array.Empty<CheapWindow>(), Threshold);

        body.Should().Contain("ÖZET");
        body.Should().Contain("En Ucuz");
        body.Should().Contain("En Pahalı");
        body.Should().Contain("Ortalama");
    }

    [Fact]
    public void Format_IncludesPracticalSuggestions()
    {
        var prices = new decimal[24];
        for (var h = 0; h < 24; h++)
            prices[h] = (h >= 10 && h <= 14) ? 100m : 1000m;

        var schedule = BuildSchedule(prices);
        var analyzer = new CheapHourAnalyzer();
        var cheapWindows = analyzer.FindCheapWindows(schedule, Threshold);

        var (_, body) = CheapHoursMessageFormatter.Format(schedule, cheapWindows, Threshold);

        body.Should().Contain("NERİ");
        body.Should().Contain("Çamaşır");
        body.Should().Contain("Bulaşık");
        body.Should().Contain("Kaçın");
    }

    [Fact]
    public void Format_SubjectContainsDateAlways()
    {
        var schedule = BuildSchedule(Enumerable.Repeat(500m, 24).ToArray());

        var (subject, _) = CheapHoursMessageFormatter.Format(schedule, Array.Empty<CheapWindow>(), Threshold);

        subject.Should().Contain("21");
        subject.Should().Contain("Haz");
    }

    // ─── Test Helpers ────────────────────────────────────────────────

    private static DailyPriceSchedule BuildSchedule(decimal[] pricesTryPerMwh)
    {
        if (pricesTryPerMwh.Length != 24)
            throw new ArgumentException("24 saatlik fiyat dizisi olmalı");

        var hours = new List<HourlyPrice>();
        for (var h = 0; h < 24; h++)
        {
            var hour = new DateTimeOffset(TestDate.Year, TestDate.Month, TestDate.Day, h, 0, 0, TimeSpan.FromHours(3));
            hours.Add(new HourlyPrice(hour: hour, priceTryPerMwh: pricesTryPerMwh[h], priceUsdPerMwh: 0m, priceEurPerMwh: 0m));
        }
        return new DailyPriceSchedule(TestDate, hours);
    }

    private static CheapWindow BuildWindow(DailyPriceSchedule schedule, int startHour, int endHour)
    {
        var hoursInWindow = schedule.Hours.Where(h => h.Hour.Hour >= startHour && h.Hour.Hour < endHour).ToList();
        return new CheapWindow(hoursInWindow);
    }
}