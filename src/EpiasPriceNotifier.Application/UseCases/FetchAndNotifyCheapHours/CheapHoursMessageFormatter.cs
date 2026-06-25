using System.Globalization;
using System.Text;
using EpiasPriceNotifier.Domain.Entities;
using EpiasPriceNotifier.Domain.ValueObjects;

namespace EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;

/// <summary>
/// Bir DailyPriceSchedule + ucuz pencere listesinden insan-okur formatda
/// bildirim mesajı üretir.
///
/// 3 katman içerir:
///   1) Bedava saatler (0 TL/kWh) — varsa
///   2) Eşik altı ucuz pencereler
///   3) Günlük özet (en ucuz / en pahalı / ortalama)
///   4) Pratik öneriler — en ucuz ve en pahalı saatler dinamik bulunur
///
/// Niye Application katmanında, Infrastructure'da değil?
/// Mesaj formatı bir "iş kuralı kararı". "Bedava saatleri öne çıkar" bir
/// domain ifadesi, Telegram/Email/Ntfy bilmek zorunda değil. Aynı format
/// her kanalda kullanılır (plain text — Telegram MarkdownV2 escape sender'da).
///
/// Static sınıf çünkü: tüm method'lar pure function. Input → output, side-effect yok.
/// </summary>
internal static class CheapHoursMessageFormatter
{
    // Türkçe formatlar — culture-aware
    private static readonly CultureInfo TrCulture = new("tr-TR");

    /// <summary>
    /// Bildirim için (subject, body) çifti üretir.
    /// Subject kısa ve subjeyi söylüyor — bildirim başlığı olarak kullanılır.
    /// Body ise tüm detaylı analiz.
    /// </summary>
    public static (string Subject, string Body) Format(
        DailyPriceSchedule schedule,
        IReadOnlyList<CheapWindow> cheapWindows,
        PriceThreshold threshold)
    {
        var subject = BuildSubject(schedule, cheapWindows);
        var body = BuildBody(schedule, cheapWindows, threshold);
        return (subject, body);
    }

    private static string BuildSubject(
        DailyPriceSchedule schedule, IReadOnlyList<CheapWindow> cheapWindows)
    {
        // Subject'i bildirim app'lerinde de görünecek şekilde kısa tut
        var hasFree = cheapWindows.Any(w => w.MinPriceTryPerKwh == 0m);

        if (hasFree)
            return $"BEDAVA elektrik saatleri var ({schedule.Date:dd MMM})";

        if (cheapWindows.Count > 0)
            return $"Ucuz elektrik saatleri ({schedule.Date:dd MMM})";

        return $"Elektrik fiyat raporu ({schedule.Date:dd MMM})";
    }

    private static string BuildBody(
        DailyPriceSchedule schedule,
        IReadOnlyList<CheapWindow> cheapWindows,
        PriceThreshold threshold)
    {
        var sb = new StringBuilder();

        // ─── Başlık ─────────────────────────────────────────────
        sb.AppendLine($"⚡ {schedule.Date.ToString("d MMMM yyyy", TrCulture)} elektrik fiyatları");
        sb.AppendLine();

        // ─── Bedava ve ucuz saatleri schedule.Hours'tan ayrı hesapla ──
        // CheapHourAnalyzer eşik altı tüm ardışık saatleri tek pencere olarak
        // gruplar — bedava saatler "ucuz" pencerenin içine gömülür. Mesajda
        // bedava ve ucuz ayrı bölümlerde gösterilmesi için, schedule.Hours'tan
        // doğrudan iki ayrı liste üretiyoruz: tamamen bedava ardışık pencereler
        // ve geri kalan (pozitif fiyatlı) ucuz pencereler.
        var freeWindows = GroupConsecutive(
            schedule.Hours.Where(h => h.PriceTryPerMwh == 0m));
        var positiveCheapWindows = GroupConsecutive(
            schedule.Hours.Where(h => h.PriceTryPerMwh > 0m
                                      && h.PriceTryPerMwh / 1000m < threshold.AmountTryPerKwh));

        // ─── 1) Bedava saatler ──────────────────────────────────
        sb.AppendLine("🆓 Bedava saatler (0 TL/kWh):");
        if (freeWindows.Count == 0)
        {
            sb.AppendLine("   (bugün yok)");
        }
        else
        {
            foreach (var w in freeWindows)
            {
                sb.AppendLine($"   • {w.StartHour:HH:mm} - {w.EndHour:HH:mm} ({w.Count} saat)");
            }
        }
        sb.AppendLine();

        // ─── 2) Ucuz saatler (eşik altı, bedavayı içermez) ──────
        sb.AppendLine($"💚 Ucuz saatler (< {threshold.AmountTryPerKwh:N2} TL/kWh):");
        if (positiveCheapWindows.Count == 0)
        {
            sb.AppendLine("   (bugün yok)");
        }
        else
        {
            foreach (var w in positiveCheapWindows)
            {
                sb.AppendLine(
                    $"   • {w.StartHour:HH:mm} - {w.EndHour:HH:mm} " +
                    $"({w.Count} saat, ort. {w.AveragePriceTryPerKwh:N2} TL/kWh)");
            }
        }
        sb.AppendLine();

        // ─── 3) Günlük özet ─────────────────────────────────────
        sb.AppendLine("📊 Günlük özet");
        sb.AppendLine(
            $"   En ucuz : {schedule.CheapestHour.Hour:HH:mm} → " +
            $"{schedule.CheapestHour.PriceTryPerKwh:N2} TL/kWh");
        sb.AppendLine(
            $"   En pahalı: {schedule.MostExpensiveHour.Hour:HH:mm} → " +
            $"{schedule.MostExpensiveHour.PriceTryPerKwh:N2} TL/kWh");
        sb.AppendLine(
            $"   Ortalama: {schedule.AverageTryPerMwh / 1000m:N2} TL/kWh");
        sb.AppendLine();

        // ─── 4) Pratik öneriler (dinamik) ───────────────────────
        AppendSuggestions(sb, schedule, cheapWindows);

        return sb.ToString();
    }

    /// <summary>
    /// En ucuz ve en pahalı saat aralıklarını dinamik bulup öneri cümleleri kurar.
    ///
    /// En ucuz pencere: ucuz pencereler arasında en uzun olanı (genelde tek pencere
    /// uzun blok halindedir; algoritma onları gruplayıp veriyor zaten).
    /// Bedava varsa bedava saatleri vurgular.
    ///
    /// En pahalı saat aralığı: günün en pahalı 4 saatini bulup pencere haline getirir.
    /// </summary>
    private static void AppendSuggestions(
        StringBuilder sb,
        DailyPriceSchedule schedule,
        IReadOnlyList<CheapWindow> cheapWindows)
    {
        sb.AppendLine("💡 Öneri");

        // En değerli pencereyi seç (öncelik: bedava → en ucuz ortalamalı)
        var bestWindow = cheapWindows
            .OrderBy(w => w.MinPriceTryPerKwh == 0m ? 0 : 1)
            .ThenBy(w => w.AveragePriceTryPerKwh)
            .FirstOrDefault();

        if (bestWindow is not null)
        {
            var priceTag = bestWindow.MinPriceTryPerKwh == 0m
                ? "BEDAVA ⚡"
                : $"ort. {bestWindow.AveragePriceTryPerKwh:N2} TL/kWh";

            sb.AppendLine(
                $"   • Çamaşır/bulaşık makinesi: " +
                $"{bestWindow.Start:HH:mm}-{bestWindow.End:HH:mm} arası ({priceTag})");
            sb.AppendLine(
                $"   • Ütü/fırın gibi yüksek tüketim: " +
                $"{bestWindow.Start:HH:mm}-{bestWindow.End:HH:mm} arası ideal");
        }
        else
        {
            sb.AppendLine("   • Bugün ucuz saat yok — mümkünse kritik cihazları erteleyebilirsin");
        }

        // En pahalı 4 saati bul, ardışıklarsa pencere yap (basit greedy)
        var expensiveHours = schedule.Hours
            .OrderByDescending(h => h.PriceTryPerMwh)
            .Take(4)
            .OrderBy(h => h.Hour)
            .ToList();

        if (expensiveHours.Count > 0)
        {
            var expStart = expensiveHours.First().Hour;
            var expEnd = expensiveHours.Last().Hour.AddHours(1);
            sb.AppendLine(
                $"   • Mümkünse {expStart:HH:mm}-{expEnd:HH:mm} arası " +
                $"yüksek tüketim cihazlarından kaçın");
        }
    }

    /// <summary>
    /// Ardışık saatleri pencereler halinde grupla. AnalyzerService'in yaptığı
    /// işin küçük bir formatter-spesifik versiyonu — bedava saatleri ayrıca
    /// göstermek için kendi gruplama mantığımız.
    ///
    /// Niye Analyzer'ı tekrar kullanmıyoruz?
    /// Analyzer threshold'a göre çalışır; bedava saatler için "threshold = 0"
    /// vermek yanıltıcı bir kullanım olur. Burada saf gruplama mantığı,
    /// formatter'ın iç işine ait.
    /// </summary>
    private static List<HourWindow> GroupConsecutive(
        IEnumerable<EpiasPriceNotifier.Domain.ValueObjects.HourlyPrice> hours)
    {
        var sorted = hours.OrderBy(h => h.Hour).ToList();
        var windows = new List<HourWindow>();

        if (sorted.Count == 0) return windows;

        var currentRun = new List<EpiasPriceNotifier.Domain.ValueObjects.HourlyPrice> { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            // Ardışık mı? Bir önceki saatten tam 1 saat sonra mı?
            var prev = currentRun[^1].Hour;
            var curr = sorted[i].Hour;

            if (curr - prev == TimeSpan.FromHours(1))
            {
                currentRun.Add(sorted[i]);
            }
            else
            {
                // Boşluk var — şu ana kadar topladığımız run'ı bir pencere yap
                windows.Add(BuildWindow(currentRun));
                currentRun = new List<EpiasPriceNotifier.Domain.ValueObjects.HourlyPrice> { sorted[i] };
            }
        }

        // Son run'ı da pencereye dönüştür
        windows.Add(BuildWindow(currentRun));

        return windows;
    }

    private static HourWindow BuildWindow(
        List<EpiasPriceNotifier.Domain.ValueObjects.HourlyPrice> run)
    {
        var start = run[0].Hour;
        var end = run[^1].Hour.AddHours(1); // end-exclusive
        var avg = run.Average(h => h.PriceTryPerMwh) / 1000m;
        return new HourWindow(start, end, run.Count, avg);
    }

    /// <summary>
    /// Formatter'a özgü hafif pencere tipi. CheapWindow'la karıştırılmasın diye
    /// ayrı tip — bunun amacı sadece display, domain anlamı yok.
    /// </summary>
    private sealed record HourWindow(
        DateTimeOffset StartHour,
        DateTimeOffset EndHour,
        int Count,
        decimal AveragePriceTryPerKwh);
}