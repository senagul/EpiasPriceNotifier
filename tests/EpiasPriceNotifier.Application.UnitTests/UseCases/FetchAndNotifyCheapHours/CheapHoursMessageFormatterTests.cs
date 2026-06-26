using EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;
using EpiasPriceNotifier.Domain.Entities;
using EpiasPriceNotifier.Domain.Services;
using EpiasPriceNotifier.Domain.ValueObjects;
using FluentAssertions;

namespace EpiasPriceNotifier.Application.UnitTests.UseCases.FetchAndNotifyCheapHours;

/// <summary>
/// CheapHoursMessageFormatter'ın çıktı formatını test eder.
///
/// Formatter pure function (static, side-effect yok, dependency yok),
/// bu yüzden test'ler input → output snapshot tarzında çok temiz.
///
/// Özellikle dikkat edilen senaryo: bedava ve ucuz saatlerin AYRI gösterilmesi.
/// Önceki implementasyonda CheapWindow tek bir pencere oluşturuyordu ve
/// içinde bedava saat varsa pencerenin TAMAMI "bedava" sınıfına düşüyordu —
/// bu bug 2026-06-21 verisiyle yakalandı ve düzeltildi. Bu davranışı koruyan
/// regression test'leri burada.
/// </summary>
public class CheapHoursMessageFormatterTests
{
    // Test'lerde tekrar tekrar kullanılan değerler için helper
    private static readonly PriceThreshold Threshold =
        PriceThreshold.FromTryPerKwh(0.30m);

    private static readonly DateOnly TestDate = new(2026, 6, 21);

    [Fact]
    public void Format_WithNoFreeOrCheapHours_ShowsBothSectionsEmpty()
    {
        // Arrange — tüm saatler eşik üstü (örn. hepsi 1.00 TL/kWh)
        var schedule = BuildSchedule(Enumerable.Repeat(1000m, 24).ToArray());
        var emptyWindows = Array.Empty<CheapWindow>();

        // Act
        var (subject, body) = CheapHoursMessageFormatter.Format(
            schedule, emptyWindows, Threshold);

        // Assert
        subject.Should().Contain("fiyat raporu"); // ne bedava ne ucuz var
        body.Should().Contain("Bedava saatler");
        body.Should().Contain("(bugün yok)"); // bedava bölümünde
        body.Should().Contain("Ucuz saatler");
        // İki "(bugün yok)" olmalı — biri bedava, biri ucuz
        var occurrences = body.Split("(bugün yok)").Length - 1;
        occurrences.Should().Be(2);
    }

    [Fact]
    public void Format_WithOnlyFreeHours_ListsFreeWindowAndEmptyCheapSection()
    {
        // Arrange — 07:00-16:00 bedava, geri kalan pahalı (eşik üstü)
        var prices = new decimal[24];
        for (var h = 0; h < 24; h++)
            prices[h] = (h >= 7 && h <= 16) ? 0m : 1000m;

        var schedule = BuildSchedule(prices);
        // CheapWindow'u 07:00-17:00 olarak oluştur (formatter cheapWindows'u
        // şu an kullanmıyor zaten — schedule.Hours'tan grupluyor — yine de
        // imza için gerçek değer geçiyoruz)
        var cheapWindows = new[]
        {
            BuildWindow(schedule, startHour: 7, endHour: 17)
        };

        // Act
        var (subject, body) = CheapHoursMessageFormatter.Format(
            schedule, cheapWindows, Threshold);

        // Assert
        subject.Should().Contain("BEDAVA");
        body.Should().Contain("07:00 - 17:00 (10 saat)"); // bedava pencere
        // Ucuz bölümünde "bugün yok" görmeli çünkü saf ucuz saat yok,
        // hepsi ya bedava ya pahalı
        var freeIdx = body.IndexOf("Bedava saatler", StringComparison.Ordinal);
        var cheapIdx = body.IndexOf("Ucuz saatler", StringComparison.Ordinal);
        var cheapSection = body.Substring(cheapIdx);
        cheapSection.Should().Contain("(bugün yok)");
    }

    [Fact]
    public void Format_WithBothFreeAndPositiveCheapHours_SeparatesIntoTwoSections()
    {
        // Arrange — bu, 21 Haziran 2026 bug senaryosunun regression testi.
        //
        // Saatler:
        //   05:00 = 0.24 TL/kWh (ucuz, eşik altı)
        //   06:00 = 0.17 TL/kWh (ucuz, eşik altı)
        //   07:00-16:00 = 0 (bedava — 10 saat)
        //   17:00 = 0.17 TL/kWh (ucuz, tek başına)
        //   geri kalan = pahalı
        //
        // Önceki bug: CheapWindow 05:00-18:00 tek pencere kuruyordu, içinde
        // bedava saat var diye TAMAMI "bedava" gösteriliyordu (13 saat).
        // Düzeltilen davranış: formatter schedule.Hours'tan iki ayrı grup yapar.
        var prices = new decimal[24];
        for (var h = 0; h < 24; h++)
        {
            prices[h] = h switch
            {
                5 => 237m,   // 0.237 TL/kWh — eşik altı (ucuz)
                6 => 171m,   // 0.171 TL/kWh — eşik altı (ucuz)
                >= 7 and <= 16 => 0m,  // bedava 10 saat
                17 => 171m,  // 0.171 TL/kWh — eşik altı (ucuz)
                _ => 1500m
            };
        }

        var schedule = BuildSchedule(prices);
        // CheapHourAnalyzer'ı gerçekten çağırıp pencereleri buluyoruz —
        // formatter ile birlikte tüm pipeline'ı test ediyoruz
        var analyzer = new CheapHourAnalyzer();
        var cheapWindows = analyzer.FindCheapWindows(schedule, Threshold);

        // Act
        var (subject, body) = CheapHoursMessageFormatter.Format(
            schedule, cheapWindows, Threshold);

        // Assert — bedava bölümünde 07:00-17:00 (10 saat) görmeli, "13 saat" GÖRMEMELI
        body.Should().Contain("07:00 - 17:00 (10 saat)");
        body.Should().NotContain("13 saat"); // regression — eski bug bunu yazıyordu

        // Ucuz bölümünde 05:00-07:00 ve 17:00-18:00 görmeli (iki ayrı pencere)
        body.Should().Contain("05:00 - 07:00");
        body.Should().Contain("17:00 - 18:00");

        // Subject "BEDAVA" içermeli çünkü bedava saat var
        subject.Should().Contain("BEDAVA");
    }

    [Fact]
    public void Format_AlwaysShowsDailySummaryWithCheapestExpensiveAndAverage()
    {
        // Arrange — değişken fiyatlar
        var prices = new decimal[24];
        for (var h = 0; h < 24; h++) prices[h] = (h + 1) * 100m;
        // 01:00 = 100, 02:00 = 200, ... 24. saat = 2400

        var schedule = BuildSchedule(prices);

        // Act
        var (_, body) = CheapHoursMessageFormatter.Format(
            schedule, Array.Empty<CheapWindow>(), Threshold);

        // Assert — Günlük özet bölümü içermeli
        body.Should().Contain("Günlük özet");
        body.Should().Contain("En ucuz");
        body.Should().Contain("En pahalı");
        body.Should().Contain("Ortalama");
    }

    [Fact]
    public void Format_IncludesPracticalSuggestions()
    {
        // Arrange
        var prices = new decimal[24];
        for (var h = 0; h < 24; h++)
            prices[h] = (h >= 10 && h <= 14) ? 100m : 1000m;

        var schedule = BuildSchedule(prices);
        var analyzer = new CheapHourAnalyzer();
        var cheapWindows = analyzer.FindCheapWindows(schedule, Threshold);

        // Act
        var (_, body) = CheapHoursMessageFormatter.Format(
            schedule, cheapWindows, Threshold);

        // Assert — "Öneri" bölümü içermeli
        body.Should().Contain("Öneri");
        body.Should().Contain("Çamaşır/bulaşık makinesi");
        body.Should().Contain("Ütü/fırın");
        body.Should().Contain("kaçın"); // en pahalı saat aralığı önerisi
    }

    [Fact]
    public void Format_SubjectContainsDateAlways()
    {
        // Arrange — herhangi bir schedule
        var schedule = BuildSchedule(Enumerable.Repeat(500m, 24).ToArray());

        // Act
        var (subject, _) = CheapHoursMessageFormatter.Format(
            schedule, Array.Empty<CheapWindow>(), Threshold);

        // Assert — subject'te tarih olmalı (kullanıcı hangi gün için bildirim
        // aldığını subject'ten görebilmeli, push notification başlığında bu kritik)
        subject.Should().Contain("21");
        subject.Should().Contain("Haz"); // "Haziran" veya "Haz" formatında
    }

    // ─── Test Helper'ları ───────────────────────────────────────────

    /// <summary>
    /// 24 saatlik fiyat dizisinden DailyPriceSchedule oluşturur.
    /// Test'lerin "arrange" bölümünü temiz tutar.
    /// </summary>
    private static DailyPriceSchedule BuildSchedule(decimal[] pricesTryPerMwh)
    {
        if (pricesTryPerMwh.Length != 24)
            throw new ArgumentException("24 saatlik fiyat dizisi olmalı");

        var hours = new List<HourlyPrice>();
        for (var h = 0; h < 24; h++)
        {
            var hour = new DateTimeOffset(
                TestDate.Year, TestDate.Month, TestDate.Day,
                h, 0, 0,
                TimeSpan.FromHours(3)); // +03:00 Türkiye saati

            hours.Add(new HourlyPrice(
                hour: hour,
                priceTryPerMwh: pricesTryPerMwh[h],
                priceUsdPerMwh: 0m,
                priceEurPerMwh: 0m));
        }

        return new DailyPriceSchedule(TestDate, hours);
    }

    /// <summary>
    /// Belirli saat aralığı için CheapWindow oluşturur.
    /// Formatter'ın imzası gereği parametre olarak veriliyor.
    /// </summary>
    private static CheapWindow BuildWindow(
        DailyPriceSchedule schedule, int startHour, int endHour)
    {
        var hoursInWindow = schedule.Hours
            .Where(h => h.Hour.Hour >= startHour && h.Hour.Hour < endHour)
            .ToList();

        return new CheapWindow(hoursInWindow);
    }
}