using System.Globalization;
using System.Text;
using EpiasPriceNotifier.Domain.Entities;
using EpiasPriceNotifier.Domain.ValueObjects;

namespace EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;

/// <summary>
/// Bir DailyPriceSchedule + ucuz pencere listesinden insan-okur formatda
/// bildirim mesajı üretir.
///
/// 4 bölüm:
///   1) Bedava saatler (0 TL/kWh)
///   2) Eşik altı ucuz pencereler
///   3) Günlük özet (en ucuz / en pahalı / ortalama)
///   4) Pratik öneriler
///
/// Niye Application katmanında?
/// Mesaj formatı bir "iş kuralı kararı". Telegram/Email/Ntfy bilmek zorunda
/// değil — formatter sadece plain text üretir, kanal-spesifik escape sender'da.
///
/// Static sınıf: tüm method'lar pure function. Input → output, side-effect yok.
/// </summary>
internal static class CheapHoursMessageFormatter
{
    private static readonly CultureInfo TrCulture = new("tr-TR");
    private const string SectionDivider = "━━━━━━━━━━━━━━━━━━━━━━━";

    public static (string Subject, string Body) Format(DailyPriceSchedule schedule, IReadOnlyList<CheapWindow> cheapWindows, PriceThreshold threshold)
    {
        var subject = BuildSubject(schedule, cheapWindows);
        var body = BuildBody(schedule, cheapWindows, threshold);
        return (subject, body);
    }

    private static string BuildSubject(DailyPriceSchedule schedule, IReadOnlyList<CheapWindow> cheapWindows)
    {
        var hasFree = cheapWindows.Any(w => w.MinPriceTryPerKwh == 0m);

        if (hasFree)
            return $"BEDAVA elektrik saatleri var ({schedule.Date:dd MMM})";

        if (cheapWindows.Count > 0)
            return $"Ucuz elektrik saatleri ({schedule.Date:dd MMM})";

        return $"Elektrik fiyat raporu ({schedule.Date:dd MMM})";
    }

    private static string BuildBody(DailyPriceSchedule schedule, IReadOnlyList<CheapWindow> cheapWindows, PriceThreshold threshold)
    {
        var sb = new StringBuilder();

        AppendHeader(sb, schedule);
        AppendFreeHoursSection(sb, schedule);
        AppendCheapHoursSection(sb, schedule, threshold);
        AppendDailySummary(sb, schedule);
        AppendSuggestions(sb, schedule, cheapWindows);

        return sb.ToString();
    }

    // ─── Header ─────────────────────────────────────────────────────
    private static void AppendHeader(StringBuilder sb, DailyPriceSchedule schedule)
    {
        sb.AppendLine("⚡ ELEKTRİK FİYAT RAPORU");
        sb.AppendLine(SectionDivider);
        sb.AppendLine($"📅 {schedule.Date.ToString("d MMMM yyyy, dddd", TrCulture)}");
        sb.AppendLine();
        sb.AppendLine();
    }

    // ─── 1) Bedava Saatler ──────────────────────────────────────────
    private static void AppendFreeHoursSection(StringBuilder sb, DailyPriceSchedule schedule)
    {
        sb.AppendLine("🆓 BEDAVA SAATLER");
        sb.AppendLine(SectionDivider);

        var freeWindows = GroupConsecutive(schedule.Hours.Where(h => h.PriceTryPerMwh == 0m));

        if (freeWindows.Count == 0)
        {
            sb.AppendLine("   (Bugün Yok)");
        }
        else
        {
            foreach (var w in freeWindows)
            {
                sb.AppendLine($"   ✨ {w.StartHour:HH:mm} — {w.EndHour:HH:mm}  ({w.Count} saat)");
            }
        }

        sb.AppendLine();
        sb.AppendLine();
    }

    // ─── 2) Ucuz Saatler ────────────────────────────────────────────
    private static void AppendCheapHoursSection(StringBuilder sb, DailyPriceSchedule schedule, PriceThreshold threshold)
    {
        sb.AppendLine("💚 UCUZ SAATLER");
        sb.AppendLine(SectionDivider);
        sb.AppendLine($"   Eşik: {threshold.AmountTryPerKwh.ToString("N2", TrCulture)} TL/kWh altı");
        sb.AppendLine();

        // CheapHourAnalyzer bedava saatleri ucuz pencerenin içine katıyor;
        // mesajda ayrı göstermek için pozitif fiyatlı ucuz saatleri yeniden gruplayıyoruz.
        var positiveCheapWindows = GroupConsecutive(schedule.Hours.Where(h => h.PriceTryPerMwh > 0m && h.PriceTryPerMwh / 1000m < threshold.AmountTryPerKwh));

        if (positiveCheapWindows.Count == 0)
        {
            sb.AppendLine("   (Bugün Yok)");
        }
        else
        {
            foreach (var w in positiveCheapWindows)
            {
                sb.AppendLine($"   🟢 {w.StartHour:HH:mm} — {w.EndHour:HH:mm}  ({w.Count} saat · ort. {w.AveragePriceTryPerKwh.ToString("N2", TrCulture)} TL/kWh)");
            }
        }

        sb.AppendLine();
        sb.AppendLine();
    }

    // ─── 3) Günlük Özet ─────────────────────────────────────────────
    private static void AppendDailySummary(StringBuilder sb, DailyPriceSchedule schedule)
    {
        sb.AppendLine("📊 GÜNLÜK ÖZET");
        sb.AppendLine(SectionDivider);
        sb.AppendLine($"   🔻 En Ucuz   :  {schedule.CheapestHour.Hour:HH:mm}  →  {schedule.CheapestHour.PriceTryPerKwh.ToString("N2", TrCulture)} TL/kWh");
        sb.AppendLine($"   🔺 En Pahalı :  {schedule.MostExpensiveHour.Hour:HH:mm}  →  {schedule.MostExpensiveHour.PriceTryPerKwh.ToString("N2", TrCulture)} TL/kWh");
        sb.AppendLine($"   ⚖️  Ortalama  :  {(schedule.AverageTryPerMwh / 1000m).ToString("N2", TrCulture)} TL/kWh");
        sb.AppendLine();
        sb.AppendLine();
    }

    // ─── 4) Pratik Öneriler ─────────────────────────────────────────
    private static void AppendSuggestions(StringBuilder sb, DailyPriceSchedule schedule, IReadOnlyList<CheapWindow> cheapWindows)
    {
        sb.AppendLine("💡 ÖNERİLER");
        sb.AppendLine(SectionDivider);

        // Öncelik: bedava → en düşük ortalamalı
        var bestWindow = cheapWindows.OrderBy(w => w.MinPriceTryPerKwh == 0m ? 0 : 1).ThenBy(w => w.AveragePriceTryPerKwh).FirstOrDefault();

        if (bestWindow is not null)
        {
            var priceTag = bestWindow.MinPriceTryPerKwh == 0m ? "BEDAVA ⚡" : $"ort. {bestWindow.AveragePriceTryPerKwh.ToString("N2", TrCulture)} TL/kWh";

            sb.AppendLine($"   ✅ Çamaşır / Bulaşık Makinesi");
            sb.AppendLine($"      {bestWindow.Start:HH:mm} — {bestWindow.End:HH:mm} arası ({priceTag})");
            sb.AppendLine();
            sb.AppendLine($"   ✅ Ütü, Fırın, Isıtıcı");
            sb.AppendLine($"      {bestWindow.Start:HH:mm} — {bestWindow.End:HH:mm} arası ideal");
        }
        else
        {
            sb.AppendLine($"   ℹ️  Bugün Ucuz Saat Yok");
            sb.AppendLine($"      Mümkünse kritik cihazları erteleyebilirsin");
        }

        // En pahalı saatler — kaçınılması önerilen aralık
        var expensiveHours = schedule.Hours.OrderByDescending(h => h.PriceTryPerMwh).Take(4).OrderBy(h => h.Hour).ToList();

        if (expensiveHours.Count > 0)
        {
            var expStart = expensiveHours.First().Hour;
            var expEnd = expensiveHours.Last().Hour.AddHours(1);
            sb.AppendLine();
            sb.AppendLine($"   ⛔ Yüksek Tüketimden Kaçın");
            sb.AppendLine($"      {expStart:HH:mm} — {expEnd:HH:mm} arası");
        }
    }

    // ─── Yardımcı: Ardışık Saat Gruplama ────────────────────────────
    /// <summary>
    /// Saatleri ardışıklığa göre pencere'lere böler. Formatter'ın bedava ve
    /// pozitif-ucuz saatleri ayrı göstermesi için kendi gruplama mantığı —
    /// CheapHourAnalyzer threshold-driven, biz burada predicate-driven gruplarız.
    /// </summary>
    private static List<HourWindow> GroupConsecutive(IEnumerable<HourlyPrice> hours)
    {
        var sorted = hours.OrderBy(h => h.Hour).ToList();
        var windows = new List<HourWindow>();

        if (sorted.Count == 0) return windows;

        var currentRun = new List<HourlyPrice> { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            var prev = currentRun[^1].Hour;
            var curr = sorted[i].Hour;

            if (curr - prev == TimeSpan.FromHours(1))
            {
                currentRun.Add(sorted[i]);
            }
            else
            {
                windows.Add(BuildWindow(currentRun));
                currentRun = new List<HourlyPrice> { sorted[i] };
            }
        }

        windows.Add(BuildWindow(currentRun));
        return windows;
    }

    private static HourWindow BuildWindow(List<HourlyPrice> run)
    {
        var start = run[0].Hour;
        var end = run[^1].Hour.AddHours(1);
        var avg = run.Average(h => h.PriceTryPerMwh) / 1000m;
        return new HourWindow(start, end, run.Count, avg);
    }

    /// <summary>Display-only window (Domain'in CheapWindow'undan ayrı).</summary>
    private sealed record HourWindow(DateTimeOffset StartHour, DateTimeOffset EndHour, int Count, decimal AveragePriceTryPerKwh);
}