using EpiasPriceNotifier.Domain.Entities;

namespace EpiasPriceNotifier.Application.Abstractions;

/// <summary>
/// EPİAŞ'tan günlük PTF (Piyasa Takas Fiyatı) çekme kontratı.
///
/// Application katmanı bu arayüze bağımlıdır, somut implementasyona değil
/// (Dependency Inversion Principle). Test sırasında mock'lanabilir,
/// production'da Infrastructure katmanındaki EpiasPriceClient implement eder.
///
/// Bu interface'in Application'da olmasının sebebi:
/// "İhtiyaç eden katman, ihtiyacını kendi tanımlar." Infrastructure değil
/// Application bu kontratı yazar — bu Hexagonal Architecture'ın "port"u.
/// </summary>
public interface IEpiasPriceClient
{
    /// <summary>
    /// Verilen tarihin 24 saatlik PTF takvimini getirir.
    /// </summary>
    /// <param name="date">PTF istenen gün (Türkiye saatiyle).</param>
    /// <param name="cancellationToken">İşlem iptali için token.</param>
    /// <returns>Günün 24 saatlik fiyat takvimi.</returns>
    /// <exception cref="System.Exception">
    /// EPİAŞ erişilemediğinde, 24 saatlik veri tam değilse veya auth başarısızsa
    /// fırlatılır. Spesifik tipler Infrastructure katmanında tanımlı.
    /// </exception>
    Task<DailyPriceSchedule> GetDailyPricesAsync(DateOnly date, CancellationToken cancellationToken = default);
}