using EpiasPriceNotifier.Domain.ValueObjects;

namespace EpiasPriceNotifier.Domain.Entities;

/// <summary>
/// Bir günün 24 saatlik elektrik takas fiyatı takvimi.
/// Aggregate root — kendi tutarlılık kurallarını içeride korur.
/// </summary>
public sealed class DailyPriceSchedule
{
    public const int ExpectedHourCount = 24;

    public DateOnly Date { get; }
    public IReadOnlyList<HourlyPrice> Hours { get; }

    public DailyPriceSchedule(DateOnly date, IEnumerable<HourlyPrice> hours)
    {
        ArgumentNullException.ThrowIfNull(hours);

        var orderedHours = hours.OrderBy(h => h.Hour).ToList();

        if (orderedHours.Count != ExpectedHourCount)
            throw new ArgumentException(
                $"Bir günde tam {ExpectedHourCount} saat olmalı, gelen: {orderedHours.Count}",
                nameof(hours));

        var allSameDay = orderedHours.All(h => DateOnly.FromDateTime(h.Hour.LocalDateTime) == date);
        if (!allSameDay)
            throw new ArgumentException(
                $"Tüm saatler {date:yyyy-MM-dd} tarihine ait olmalı",
                nameof(hours));

        var distinctHours = orderedHours.Select(h => h.Hour.Hour).Distinct().Count();
        if (distinctHours != ExpectedHourCount)
            throw new ArgumentException(
                "24 saatin tamamı farklı olmalı, tekrar eden saat var",
                nameof(hours));

        Date = date;
        Hours = orderedHours.AsReadOnly();
    }

    public decimal AverageTryPerMwh => Hours.Average(h => h.PriceTryPerMwh);

    public decimal MinTryPerMwh => Hours.Min(h => h.PriceTryPerMwh);

    public decimal MaxTryPerMwh => Hours.Max(h => h.PriceTryPerMwh);

    public HourlyPrice CheapestHour =>
        Hours.OrderBy(h => h.PriceTryPerMwh).First();

    public HourlyPrice MostExpensiveHour =>
        Hours.OrderByDescending(h => h.PriceTryPerMwh).First();

    public IEnumerable<HourlyPrice> HoursCheaperThanPerKwh(decimal thresholdTryPerKwh) =>
        Hours.Where(h => h.IsCheaperThanPerKwh(thresholdTryPerKwh));

    public override string ToString() =>
        $"{Date:yyyy-MM-dd}: ortalama {AverageTryPerMwh:N2} TL/MWh, " +
        $"min {MinTryPerMwh:N2} (saat {CheapestHour.Hour:HH:mm}), " +
        $"max {MaxTryPerMwh:N2} (saat {MostExpensiveHour.Hour:HH:mm})";
}