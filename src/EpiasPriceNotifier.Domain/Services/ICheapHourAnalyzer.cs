using EpiasPriceNotifier.Domain.Entities;
using EpiasPriceNotifier.Domain.ValueObjects;

namespace EpiasPriceNotifier.Domain.Services;

/// <summary>
/// 24 saatlik fiyat takvimi içinden ardışık ucuz pencereleri (CheapWindow) çıkarır.
/// Saf domain logic — dış dünyaya bağımlı değildir.
/// </summary>
public interface ICheapHourAnalyzer
{
    IReadOnlyList<CheapWindow> FindCheapWindows(DailyPriceSchedule schedule, PriceThreshold threshold);
}