using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.Services;
using EpiasPriceNotifier.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;

/// <summary>
/// FetchAndNotifyCheapHoursCommand'ı işleyen handler.
///
/// Use case adımları:
///   1) EPİAŞ'tan günün PTF takvimini çek (IEpiasPriceClient)
///   2) Threshold'u config'ten al (IPriceThresholdProvider)
///   3) CheapHourAnalyzer ile ucuz pencereleri bul (saf domain logic)
///   4) Recipient listesini config'ten çevir (IRecipientProvider)
///   5) Mesajı formatla (CheapHoursMessageFormatter)
///   6) Dispatcher ile gönder (INotificationDispatcher)
///
/// Bu handler tüm parçaları birleştiriyor ama HIÇBIRINI direk implement etmiyor.
/// "Plumbing" katmanı — orchestration ve composition. Bu yüzden:
/// - HttpClient yok
/// - JSON parse yok
/// - DB access yok
/// - SMTP/Telegram API yok
///
/// Hepsi inject edilen arayüzlerin arkasında. Test'te bu handler 6 interface'i
/// mock'layıp iş akışını izole olarak test edebilirsin.
///
/// CheapHourAnalyzer interface DEĞİL, somut sınıf inject ediliyor. Niye?
/// Saf domain logic, dependency yok, mock'lamak yerine gerçeğini kullanmak daha
/// doğru — analyzer pratikte test edilemiyor değil, zaten test edilmiş
/// (4 unit test'i var). Bu, "test for behavior, not for implementation"
/// prensibinin bir uygulaması.
/// </summary>
public sealed class FetchAndNotifyCheapHoursHandler
    : IRequestHandler<FetchAndNotifyCheapHoursCommand>
{
    private readonly IEpiasPriceClient _priceClient;
    private readonly ICheapHourAnalyzer _analyzer;
    private readonly INotificationDispatcher _dispatcher;
    private readonly IPriceThresholdProvider _thresholdProvider;
    private readonly IRecipientProvider _recipientProvider;
    private readonly INotificationLogRepository _logRepository;
    private readonly ILogger<FetchAndNotifyCheapHoursHandler> _logger;

    public FetchAndNotifyCheapHoursHandler(
        IEpiasPriceClient priceClient,
        ICheapHourAnalyzer analyzer,
        INotificationDispatcher dispatcher,
        IPriceThresholdProvider thresholdProvider,
        IRecipientProvider recipientProvider,
        INotificationLogRepository logRepository,
        ILogger<FetchAndNotifyCheapHoursHandler> logger)
    {
        _priceClient = priceClient;
        _analyzer = analyzer;
        _dispatcher = dispatcher;
        _thresholdProvider = thresholdProvider;
        _recipientProvider = recipientProvider;
        _logRepository = logRepository;
        _logger = logger;
    }

    public async Task Handle(
          FetchAndNotifyCheapHoursCommand command,
          CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "FetchAndNotifyCheapHours başladı: {Date}", command.Date);

        // ─── Idempotency check ──────────────────────────────────────────
        // Bu tarih için daha önce başarılı bildirim gönderilmiş mi?
        // Quartz'ın disallow-concurrent-execution'ı bir tetiklemeyi engelliyor
        // ama uygulama restart'ında veya manuel tetiklemede aynı tarihe ikinci
        // bildirim atılma riski var. Repository'den sorup erken çıkıyoruz.
        if (await _logRepository.HasSentForDateAsync(command.Date, cancellationToken))
        {
            _logger.LogInformation(
                "{Date} için bildirim zaten gönderilmiş, atlanıyor (idempotency)",
                command.Date);
            return;
        }

        // 1) EPİAŞ'tan PTF takvimini çek
        var schedule = await _priceClient.GetDailyPricesAsync(
            command.Date, cancellationToken);

        // 2) Threshold'u config'ten al
        var threshold = _thresholdProvider.GetThreshold();

        // 3) Ucuz pencereleri bul — saf domain logic
        var cheapWindows = _analyzer.FindCheapWindows(schedule, threshold);

        _logger.LogInformation(
            "Analiz tamamlandı: {WindowCount} ucuz pencere bulundu (eşik: {Threshold} TL/kWh)",
            cheapWindows.Count, threshold.AmountTryPerKwh);

        // 4) Recipient listesini hazırla
        var recipients = _recipientProvider.GetRecipients().ToList();
        if (recipients.Count == 0)
        {
            _logger.LogWarning(
                "Bildirim gönderilecek recipient tanımlı değil, akış sessizce sonlanıyor");
            return;
        }

        // 5) Mesajı formatla
        var (subject, body) = CheapHoursMessageFormatter.Format(
            schedule, cheapWindows, threshold);

        _logger.LogDebug("Bildirim mesajı hazırlandı: {Subject}", subject);

        // 6) Dispatcher ile gönder
        await _dispatcher.SendAsync(recipients, subject, body, cancellationToken);

        // ─── Idempotency record ─────────────────────────────────────────
        // Başarılı gönderimden SONRA log kaydı ekle. Sıralama önemli:
        // - Eğer önce kaydetseydik ve gönderim patlasaydı, kullanıcı mesaj
        //   almadan idempotency etkin olurdu → kullanıcı hiçbir şey almazdı.
        // - Şimdiki sırada: gönderim patlasa exception fırlar, log kaydı yok,
        //   bir sonraki tetiklemede tekrar denenebilir.
        await _logRepository.RecordSentAsync(
            command.Date,
            recipients.Count,
            subject,
            cancellationToken);

        _logger.LogInformation(
            "FetchAndNotifyCheapHours tamamlandı: {Date}, {RecipientCount} alıcı",
            command.Date, recipients.Count);
    }
}

/// <summary>
/// Threshold değerini sağlayan kontrat.
/// Bunu ayrı bir abstraction yaptım çünkü:
/// - Config'ten gelse de olur, DB'den gelse de
/// - Test'te basitçe mock'lanır
/// - Yarın "kişi başı threshold" özelliği gelirse imza genişletmek kolay
/// Implementasyon Infrastructure katmanında.
/// </summary>
public interface IPriceThresholdProvider
{
    PriceThreshold GetThreshold();
}

/// <summary>
/// Recipient listesini sağlayan kontrat.
/// Config'ten okuma + Domain Recipient nesnesine dönüştürme detayı
/// Application'dan saklanır. Application sadece "kime gönderecek"
/// listesini ister, kaynağı bilmez.
/// </summary>
public interface IRecipientProvider
{
    IEnumerable<Recipient> GetRecipients();
}