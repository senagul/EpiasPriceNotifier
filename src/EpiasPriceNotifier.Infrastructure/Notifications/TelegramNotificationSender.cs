using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Application.Common.Exceptions;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace EpiasPriceNotifier.Infrastructure.Notifications;

/// <summary>
/// Telegram kanalı için sender. Telegram.Bot NuGet kütüphanesi ile Bot API'sine
/// HTTPS üzerinden konuşur.
///
/// Recipient → chatId mapping'i config'ten (TelegramOptions.ChatIds) gelir.
/// Sender geldiğinde recipient.Name ile dictionary'den chatId'yi bulur.
///
/// Niye ITelegramBotClient'ı kendimiz wrap'liyoruz?
/// - Test edilebilirlik: INotificationSender'ı mock'lamak kolay
/// - Exception standardizasyonu: Telegram'a özgü ApiRequestException'ı
///   bizim domain'in NotificationSendException'ına çeviriyoruz —
///   GlobalExceptionHandler ve Dispatcher tek tip exception görür
/// - DI kolaylığı: bağımlılık zincirini açık tutuyoruz
/// </summary>
public sealed class TelegramNotificationSender : INotificationSender
{
    private readonly ITelegramBotClient _botClient;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramNotificationSender> _logger;

    public NotificationChannel Channel => NotificationChannel.Telegram;

    public TelegramNotificationSender(
        ITelegramBotClient botClient,
        IOptions<NotificationOptions> options,
        ILogger<TelegramNotificationSender> logger)
    {
        _botClient = botClient;
        _options = options.Value.Telegram;
        _logger = logger;
    }

    public async Task SendAsync(
        Recipient recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        // 1. Bu recipient gerçekten Telegram istiyor mu? (Defansif kontrol)
        // Dispatcher zaten kontrol etmiş olmalı, ama Sender da kendi başına
        // çalıştırılabilir; "double-guard" pratiği.
        if (!recipient.ReceivesVia(NotificationChannel.Telegram))
        {
            _logger.LogWarning(
                "Recipient {Name} Telegram kanalını istemiyor, gönderim atlanıyor",
                recipient.Name);
            return;
        }

        // 2. Recipient adından chat ID'yi bul
        if (!_options.ChatIds.TryGetValue(recipient.Name, out var chatId))
        {
            // Config eksikliği — bu kanal isteyen recipient için chatId
            // tanımlanmamış. Patlatma, çünkü diğer kanallar çalışsın.
            // Sadece logla, dispatcher hata olarak yakalamasın.
            _logger.LogError(
                "Recipient {Name} Telegram istiyor ama TelegramOptions.ChatIds'de chat ID tanımlı değil",
                recipient.Name);
            throw new NotificationSendException(
                NotificationChannel.Telegram,
                recipient.Name,
                $"Recipient '{recipient.Name}' için Telegram chat ID tanımlı değil");
        }

        // 3. Mesajı oluştur — Telegram MarkdownV2 destekler
        // Subject'i bold yapıyoruz (* * arası)
        var fullText = $"*{EscapeMarkdown(subject)}*\n\n{EscapeMarkdown(body)}";

        // 4. Gönder
        try
        {
            await _botClient.SendMessage(
                chatId: chatId,
                text: fullText,
                parseMode: ParseMode.MarkdownV2,
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Telegram bildirim gönderildi: {Recipient} (chatId: {ChatId})",
                recipient.Name, chatId);
        }
        catch (ApiRequestException ex)
        {
            // Telegram'a özgü exception'ı domain exception'ına çevir
            _logger.LogError(ex,
                "Telegram bildirim gönderilemedi: {Recipient}",
                recipient.Name);
            throw new NotificationSendException(
                NotificationChannel.Telegram,
                recipient.Name,
                $"Telegram API hatası: {ex.Message}",
                ex);
        }
        catch (Exception ex) when (ex is not NotificationSendException)
        {
            // Beklenmedik exception (network, vs.)
            _logger.LogError(ex,
                "Telegram bildirim gönderilirken beklenmedik hata: {Recipient}",
                recipient.Name);
            throw new NotificationSendException(
                NotificationChannel.Telegram,
                recipient.Name,
                $"Telegram gönderim hatası: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Telegram MarkdownV2'de bazı karakterler reserved — escape edilmezse
    /// API "Bad Request: can't parse entities" diyerek mesajı reddeder.
    /// Örnek: '.', '-', '!' karakterleri standart metinde sık geçer.
    /// </summary>
    private static string EscapeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Telegram MarkdownV2'nin reserved karakterleri:
        // _ * [ ] ( ) ~ ` > # + - = | { } . !
        char[] reserved = ['_', '*', '[', ']', '(', ')', '~', '`', '>', '#',
                           '+', '-', '=', '|', '{', '}', '.', '!'];

        var result = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (Array.IndexOf(reserved, c) >= 0)
                result.Append('\\');
            result.Append(c);
        }
        return result.ToString();
    }
}