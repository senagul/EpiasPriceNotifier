using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Application.Common.Exceptions;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace EpiasPriceNotifier.Infrastructure.Notifications;

/// <summary>
/// Email kanalı için sender. Gmail SMTP üzerinden MailKit ile gönderir.
///
/// Niye MailKit, System.Net.Mail.SmtpClient değil?
/// Microsoft'un kendi resmi önerisi: System.Net.Mail.SmtpClient sınıfı
/// "obsolete for new development" olarak işaretli. Modern TLS'i tam
/// desteklemiyor, async API'si zayıf, bazı edge case'lerde hatalar var.
/// MailKit defacto standart, Microsoft kendisi MailKit'e yönlendiriyor.
///
/// Niye Gmail App Password?
/// Gmail "less secure apps" desteğini Mayıs 2022'de kapattı. Normal
/// hesap şifresi ile SMTP login artık çalışmıyor. 2FA + App Password
/// tek geçerli yol. App Password 16 haneli, sadece bu uygulama için
/// üretilir, istenirse revoke edilebilir — repo'ya sızsa bile sınırlı zarar.
///
/// Niye her gönderimde yeni SmtpClient yaratıyoruz?
/// MailKit'in SmtpClient'ı thread-safe değil ve kalıcı connection tutmuyor.
/// Bizim use case'de (günde 1-2 mesaj) her seferinde yeniden bağlanmak
/// performans sorunu değil. High-volume olsaydı pool tutmak gerekirdi
/// ama o noktada queue-based bir mail servisi (örn. SendGrid) daha doğru olur.
/// </summary>
public sealed class EmailNotificationSender : INotificationSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<EmailNotificationSender> _logger;

    public NotificationChannel Channel => NotificationChannel.Email;

    public EmailNotificationSender(
        IOptions<NotificationOptions> options,
        ILogger<EmailNotificationSender> logger)
    {
        _options = options.Value.Email;
        _logger = logger;
    }

    public async Task SendAsync(
        Recipient recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        // 1. Bu recipient gerçekten Email istiyor mu? (defansif)
        if (!recipient.ReceivesVia(NotificationChannel.Email))
        {
            _logger.LogWarning(
                "Recipient {Name} Email kanalını istemiyor, gönderim atlanıyor",
                recipient.Name);
            return;
        }

        // 2. Recipient adından email adresini bul
        if (!_options.Addresses.TryGetValue(recipient.Name, out var toAddress)
            || string.IsNullOrWhiteSpace(toAddress))
        {
            _logger.LogError(
                "Recipient {Name} Email istiyor ama EmailOptions.Addresses'ta adres tanımlı değil",
                recipient.Name);
            throw new NotificationSendException(
                NotificationChannel.Email,
                recipient.Name,
                $"Recipient '{recipient.Name}' için Email adresi tanımlı değil");
        }

        // 3. MimeMessage oluştur — MIME formatı, multipart destekler
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.From));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;

        // Body'i text/plain olarak koyuyoruz. HTML istesek MimeKit.BodyBuilder
        // ile multipart yapardık (HtmlBody + TextBody). Şimdilik plain text
        // yeterli — basit, spam filtrelerinden geçer, her client'ta okunur.
        message.Body = new TextPart("plain") { Text = body };

        // 4. Gönder
        try
        {
            using var smtp = new SmtpClient();

            // STARTTLS — port 587'de plain bağlantı başlar, sonra TLS'e geçer.
            // Gmail bunu zorunlu tutuyor; SslOnConnect (port 465) da var
            // ama Gmail önce 587'yi öneriyor.
            await smtp.ConnectAsync(
                _options.SmtpHost,
                _options.SmtpPort,
                SecureSocketOptions.StartTls,
                cancellationToken);

            // Authenticate: gönderici email + 16 haneli App Password
            await smtp.AuthenticateAsync(
                _options.From,
                _options.AppPassword,
                cancellationToken);

            await smtp.SendAsync(message, cancellationToken);

            // Sunucuya QUIT gönder — connection clean kapansın
            await smtp.DisconnectAsync(true, cancellationToken);

            _logger.LogInformation(
                "Email bildirim gönderildi: {Recipient} ({Address})",
                recipient.Name, toAddress);
        }
        catch (AuthenticationException ex)
        {
            // App Password yanlış veya 2FA kapalı — bu hata kullanıcı sorunu,
            // tekrar denemek çözüm değil. Açıkça log'la.
            _logger.LogError(ex,
                "Email SMTP authentication başarısız. App Password doğru mu, 2FA açık mı?");
            throw new NotificationSendException(
                NotificationChannel.Email,
                recipient.Name,
                "SMTP authentication başarısız (App Password / 2FA kontrol et)",
                ex);
        }
        catch (Exception ex) when (ex is not NotificationSendException)
        {
            // Network, DNS, timeout vs.
            _logger.LogError(ex,
                "Email gönderilemedi: {Recipient}",
                recipient.Name);
            throw new NotificationSendException(
                NotificationChannel.Email,
                recipient.Name,
                $"Email gönderim hatası: {ex.Message}",
                ex);
        }
    }
}