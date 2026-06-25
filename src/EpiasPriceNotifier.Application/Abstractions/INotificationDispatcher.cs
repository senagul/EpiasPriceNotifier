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
///
/// Use case'ler (ileride MediatR handler'ları) bu interface'i inject edecek
/// ve `await dispatcher.SendAsync(recipients, subject, body)` diyecek.
/// Hangi kanaldan kime gittiği, exception yakalama, paralelizasyon — hepsi
/// dispatcher'ın içinde, use case temiz kalır.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Verilen recipient'lara, her birinin seçtiği kanallar üzerinden
    /// aynı subject + body ile mesaj gönderir.
    /// </summary>
    /// <param name="recipients">Mesajın gideceği kişiler.</param>
    /// <param name="subject">Tüm kanallar için kısa başlık.</param>
    /// <param name="body">Tüm kanallar için mesaj gövdesi.</param>
    /// <param name="cancellationToken">İptal token'ı.</param>
    /// <remarks>
    /// Bu method exception fırlatmaz — bireysel kanal patlamaları içeride
    /// loglanır. Toplu bir hata söz konusu olduğunda da return tipinde
    /// sinyal veririz (gelecek genişletme).
    /// </remarks>
    Task SendAsync(
        IEnumerable<Recipient> recipients,
        string subject,
        string body,
        CancellationToken cancellationToken = default);
}