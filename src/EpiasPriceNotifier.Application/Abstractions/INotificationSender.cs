using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;

namespace EpiasPriceNotifier.Application.Abstractions;

/// <summary>
/// Tek bir bildirim kanalı (Telegram, Email, Ntfy, vs.) için sender kontratı.
///
/// Her sender:
/// - Hangi kanal olduğunu beyan eder (Channel property)
/// - Bir Recipient alır, kendi mapping'inden gerçek hedefi bulur
///   (Telegram → chatId, Email → adres, Ntfy → topic)
/// - Subject ve body ile mesaj gönderir
///
/// Sender'lar BIRBIRINI BILMEZ. Sadece kendi kanallarından sorumlu.
/// Hangi recipient'a hangi kanaldan gidileceğini Dispatcher kararlaştırır.
/// Bu Strategy pattern'i — her sender bir strateji.
///
/// Niye böyle?
/// - Yarın Discord eklemek istesem: yeni IDiscordNotificationSender yazarım, 
///   DI'a kaydederim, bitti. Mevcut hiçbir kod değişmez (Open/Closed).
/// - Test ederken: dispatcher'ı test ederken sender'ları mock'layabilirim
/// - Hata izolasyonu: bir kanal patlasa diğerleri çalışmaya devam eder
/// </summary>
public interface INotificationSender
{
    /// <summary>
    /// Bu sender'ın hangi kanalı yönettiği. Dispatcher recipient'ın kanal
    /// listesini bu property üzerinden eşleştirir.
    /// </summary>
    NotificationChannel Channel { get; }

    /// <summary>
    /// Verilen recipient'a bu kanal üzerinden mesaj gönderir.
    /// </summary>
    /// <param name="recipient">Mesajın gideceği kişi.</param>
    /// <param name="subject">Başlık / kısa özet (örn. e-posta subject, Telegram title).</param>
    /// <param name="body">Asıl mesaj içeriği.</param>
    /// <param name="cancellationToken">İptal token'ı.</param>
    /// <exception cref="Common.Exceptions.NotificationSendException">
    /// Bu kanaldan gönderim başarısız olursa fırlatılır.
    /// Dispatcher yakalar, diğer kanallara devam eder.
    /// </exception>
    Task SendAsync(
        Recipient recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default);
}