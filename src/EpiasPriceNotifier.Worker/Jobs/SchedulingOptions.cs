namespace EpiasPriceNotifier.Worker.Jobs;

/// <summary>
/// Job tetikleme ayarları (cron, timezone).
/// appsettings.json'daki "Scheduling" bölümüne map'lenir.
///
/// Cron format Quartz'a özgü (Unix cron'dan farklı):
///   "saniye dakika saat ayın_günü ay haftanın_günü"
/// Örnek: "0 0 15 * * ?" = her gün 15:00:00
///
/// Niye `?` haftanın gününde?
/// "ayın günü" ve "haftanın günü" alanlarından ikisini birden bilgi
/// vermek mantıksız (çakışabilir). Birini `?` yaparsın, diğeri spesifik
/// olur. Quartz bunu zorunlu kılıyor.
///
/// Niye SchedulingOptions, QuartzOptions değil?
/// Quartz kütüphanesinin kendi QuartzOptions tipi var (Quartz.QuartzOptions);
/// aynı isim çakışma yaratıyor. SchedulingOptions hem daha açıklayıcı
/// (uygulamamızdaki "scheduling" konseptini ifade eder) hem çakışmıyor.
/// </summary>
public sealed class SchedulingOptions
{
    /// <summary>appsettings.json'daki bölüm adı.</summary>
    public const string SectionName = "Scheduling";

    /// <summary>
    /// Cron pattern. Default her gün 15:00 (EPİAŞ ertesi günün fiyatlarını
    /// ~14:00 civarı yayımlıyor; 1 saat buffer ile güvenli).
    /// </summary>
    public string FetchAndNotifyCron { get; init; } = "0 0 15 * * ?";

    /// <summary>
    /// Quartz scheduler'ın çalıştığı timezone. EPİAŞ Türkiye saati ile yayım
    /// yaptığı için job'ı da Türkiye saatine göre tetiklemek uygun.
    /// Server UTC'de bile olsa cron Türkiye saati üzerinden çalışır.
    /// Linux container'da bu ID 'Europe/Istanbul' olur.
    /// </summary>
    public string TimeZone { get; init; } = "Turkey Standard Time";
}