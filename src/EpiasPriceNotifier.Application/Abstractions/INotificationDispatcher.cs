using EpiasPriceNotifier.Domain.ValueObjects;

namespace EpiasPriceNotifier.Application.Abstractions;

/// <summary>
/// Recipient listesi ve mesajı alıp, her recipient'ın kendi seçtiği
/// kanallar üzerinden bildirim gönderimini koordine eder.
///
/// Dispatcher'ın sorumluluğu:
/// 1) Her recipient için her seçili kanala uygun sender'ı bul
/// 2) Paralel olarak gönderimleri tetikle (hızlı olsun)
/// 3) Bir gönderim patlasa (NotificationSendException) yutup loglar,
///    diğer gönderimlere devam eder — fault isolation
/// 4) Sonunda DispatchResult döner — handler post-dispatch kararını verir
///
/// Use case'ler (MediatR handler'ları) bu interface'i inject edip
/// `var result = await dispatcher.SendAsync(...)` der.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Verilen recipient'lara, her birinin seçtiği kanallar üzerinden
    /// aynı subject + body ile mesaj gönderir.
    /// </summary>
    /// <returns>
    /// Başarılı ve başarısız gönderim sayılarını içeren DispatchResult.
    /// Caller bu sonuca bakarak post-dispatch kararını verebilir
    /// (örn. idempotency kaydı atıp atmamak).
    /// </returns>
    Task<DispatchResult> SendAsync(
        IEnumerable<Recipient> recipients,
        string subject,
        string body,
        CancellationToken cancellationToken = default);
}