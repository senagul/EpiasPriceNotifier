using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Application.Common.Exceptions;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace EpiasPriceNotifier.Infrastructure.Notifications;

/// <summary>
/// Tüm INotificationSender'ları DI'dan toplar, recipient listesi geldiğinde
/// her birine uygun sender'lara paralel olarak dağıtır.
///
/// Çekirdek özellikler:
///   1) Paralelizasyon: tüm (recipient, channel) çiftleri aynı anda gönderilir.
///      3 kişi × 2 kanal = 6 görev, sıralı 6 saniye sürerken paralel ~1 saniye.
///   2) Fault isolation: bir gönderim patlasa (NotificationSendException),
///      yutulup loglanır, diğer 5 gönderim etkilenmez. "En kötü ihtimalle
///      kullanıcının bir kanalı çalışmaz, diğerleri çalışır" garantisi.
///   3) Exception fırlatmaz: dispatcher sessizce çalışır, hatalar log'a düşer.
///      Use case'ler "gönderim emrini verdim" der, gerisi infrastructure işi.
///
/// DI'dan IEnumerable&lt;INotificationSender&gt; alıyor — tüm registered
/// sender'lar otomatik geliyor. Yarın Discord sender'ı yazsam DI'a eklemem
/// yeterli; dispatcher kodu hiç değişmez. Open/Closed Principle.
/// </summary>
public sealed class NotificationDispatcher : INotificationDispatcher
{
    /// <summary>
    /// Channel → Sender lookup'ı. Sender'lar Channel property'lerini ilan
    /// ettiği için constructor'da bir kez Dictionary'ye çeviriyoruz.
    /// O(1) erişim, runtime'da hızlı.
    /// </summary>
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationSender> _sendersByChannel;

    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(IEnumerable<INotificationSender> senders,ILogger<NotificationDispatcher> logger)
    {
        // ToDictionary key seçicisi: senderın ilan ettiği Channel.
        // Aynı Channel için iki sender register edilseydi burada exception
        // fırlatırdı — bu kasıtlı; configuration hatasını erken yakalamak iyi.
        _sendersByChannel = senders.ToDictionary(s => s.Channel);
        _logger = logger;

        _logger.LogInformation("NotificationDispatcher kuruldu, kayıtlı sender'lar: {Channels}",string.Join(", ", _sendersByChannel.Keys));
    }

    public async Task<DispatchResult> SendAsync(IEnumerable<Recipient> recipients,string subject,string body,CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        var recipientList = recipients.ToList();

        if (recipientList.Count == 0)
        {
            _logger.LogWarning("Bildirim gönderilecek recipient yok");
            return DispatchResult.Empty;
        }

        // Her task bir bool döner: true = başarı, false = fail.
        // Sonunda Task.WhenAll ile toplayıp DispatchResult oluşturuyoruz.
        var sendTasks = new List<Task<bool>>();

        foreach (var recipient in recipientList)
        {
            foreach (var channel in recipient.Channels)
            {
                if (!_sendersByChannel.TryGetValue(channel, out var sender))
                {
                    _logger.LogWarning("Recipient {Recipient} {Channel} istiyor ama bu kanal için sender kayıtlı değil",recipient.Name, channel);
                    continue;
                }

                sendTasks.Add(SafeSendAsync(sender, recipient, subject, body, cancellationToken));
            }
        }

        if (sendTasks.Count == 0)
        {
            // Recipient var ama hiçbiri kayıtlı kanal istemedi
            return DispatchResult.Empty;
        }

        // Paralel çalıştır, her task'ın bool sonucunu topla
        var results = await Task.WhenAll(sendTasks);

        var successCount = results.Count(r => r);
        var failureCount = results.Length - successCount;

        _logger.LogInformation("Bildirim gönderimi tamamlandı: {RecipientCount} alıcı, {Success} başarılı, {Failed} başarısız",recipientList.Count, successCount, failureCount);

        return new DispatchResult(successCount, failureCount);
    }

    /// <summary>
    /// Tek bir sender çağrısını yutucu wrapper içinde çalıştırır.
    /// Başarı durumunda true, NotificationSendException veya beklenmedik
    /// exception durumunda false döner. OperationCanceledException re-throw
    /// edilir (iptal bilinçli aksiyon, hata değil).
    /// </summary>
    private async Task<bool> SafeSendAsync(INotificationSender sender,Recipient recipient,string subject,string body,CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendAsync(recipient, subject, body, cancellationToken);
            return true;
        }
        catch (NotificationSendException ex)
        {
            _logger.LogError(ex,"Bildirim gönderim hatası: {Channel} → {Recipient}",ex.Channel, ex.RecipientName);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Beklenmedik bildirim hatası: {Channel} → {Recipient}", sender.Channel, recipient.Name);
            return false;
        }
    }
}