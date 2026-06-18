using EpiasPriceNotifier.Domain.Entities;
using EpiasPriceNotifier.Domain.ValueObjects;

namespace EpiasPriceNotifier.Domain.Services;

/// <summary>
/// 24 saatlik fiyat takvimi içinden ardışık ucuz pencereleri (CheapWindow) çıkarır.
/// Saf domain logic — dış dünyaya hiçbir bağımlılığı yoktur (HttpClient, EF, vs. yok).
/// Bu sayede yüzlerce edge-case ms'ler içinde test edilebilir.
/// </summary>
public sealed class CheapHourAnalyzer : ICheapHourAnalyzer
{
    /// <summary>
    /// Verilen takvim ve eşik için ardışık ucuz saat gruplarını döndürür.
    /// </summary>
    /// <param name="schedule">Bir günün 24 saatlik fiyat takvimi (aggregate root).</param>
    /// <param name="threshold">Hangi fiyatın altı "ucuz" sayılacağını belirten eşik.</param>
    /// <returns>
    /// Hiç ucuz saat yoksa boş liste; aksi halde her ardışık ucuz grup için bir CheapWindow.
    /// </returns>
    /// <remarks>
    /// Algoritma O(n), tek geçişli (single-pass). 24 elemanlı liste için pratikte
    /// O(1) sayılır — ama O(n) yazmak iş arkadaşlarına saygıdır.
    /// </remarks>
    public IReadOnlyList<CheapWindow> FindCheapWindows(
        DailyPriceSchedule schedule,
        PriceThreshold threshold)
    {
        // Defensive programming: aggregate root null olamaz. ArgumentNullException.ThrowIfNull
        // .NET 6+ ile gelen kısa yol — eskiden 3 satır kod yazılırdı.
        // PriceThreshold readonly record struct olduğu için null check'e gerek yok (value type).
        ArgumentNullException.ThrowIfNull(schedule);

        // Sonuç biriktiricisi — bulunan CheapWindow'lar buraya eklenecek.
        var windows = new List<CheapWindow>();

        // "currentRun" şu anda inşa ettiğimiz ardışık ucuz grubu temsil eder.
        // Eşik altında bir saat geldikçe büyür, eşik üstü saat gelince kapatılır.
        var currentRun = new List<HourlyPrice>();

        // Tek geçiş: 24 saati sırayla gez. DailyPriceSchedule constructor'ı zaten
        // saatleri sıralı (ascending) garantiledi, yeniden sıralama gerekmiyor.
        foreach (var hour in schedule.Hours)
        {
            if (threshold.Includes(hour))
            {
                // Bu saat ucuz → mevcut gruba ekle. Henüz CheapWindow yaratmıyoruz çünkü
                // belki sıradaki saat de ucuzdur ve grup büyümeye devam edecek.
                currentRun.Add(hour);
            }
            else if (currentRun.Count > 0)
            {
                // Bu saat pahalı VE elimizde aktif bir ucuz grup vardı.
                // Grup burada bitti → CheapWindow olarak finalize et, sonuca ekle.
                windows.Add(new CheapWindow(currentRun));

                // Yeni boş liste başlat. Mevcut liste'yi Clear() etmek YANLIŞ olurdu çünkü
                // CheapWindow ctor'una az önce verdiğimiz referansı bozardık (aliasing bug).
                // Yeni bir List<T> instance'ı yaratmak güvenli.
                currentRun = new List<HourlyPrice>();
            }
            // else: hem saat pahalı hem currentRun zaten boş → yapacak bir şey yok, devam.
        }

        // ÖNEMLİ EDGE CASE: gün ucuz saatlerle bitiyorsa (örn. 22:00, 23:00 ucuz),
        // foreach içindeki "pahalı saat gelince kapat" mantığı tetiklenmedi.
        // Bu son kapanmamış grubu burada manuel kapatıyoruz.
        if (currentRun.Count > 0)
            windows.Add(new CheapWindow(currentRun));

        // Dışarıya readonly döndürüyoruz — kullanıcı Add/Remove yapamasın.
        // AsReadOnly() wrapper döner, kopya değil — performanslı.
        return windows.AsReadOnly();
    }
}