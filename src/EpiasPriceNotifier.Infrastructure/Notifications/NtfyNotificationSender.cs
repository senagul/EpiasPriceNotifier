using System.Text;
using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Application.Common.Exceptions;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EpiasPriceNotifier.Infrastructure.Notifications;

/// <summary>
/// ntfy.sh kanalı için sender. Hesapsız, API-key'siz push notification servisi.
///
/// Protokol son derece basit: bir HTTP POST. Body = mesaj metni.
/// Topic adı URL'in path'inde. Header'lar opsiyonel metadata için
/// (Title, Priority, Tags, vs.).
///
/// Niye NuGet SDK kullanmıyoruz?
/// ntfy.sh için resmi .NET SDK yok — gerek de yok, ham HttpClient yeterli.
/// SDK ekledikçe transitive dependency, sürüm kavgaları, gereksiz abstraction
/// gelir. Basit bir POST için 10 satır kod en doğru yol.
///
/// Yedek değil, eşit kanal:
/// Telegram/Email her zaman çalışsın da ntfy "olursa olur" diye düşünme.
/// ntfy.sh telefondaki kilit ekranına anında düşer; bazen SMS'ten bile
/// hızlıdır. SMS'in bedava alternatifi tam olarak budur.
///
/// Güvenlik:
/// Topic adı = paylaşılan sır. Tahmin edilirse o topic'i kim subscribe ederse
/// senin mesajlarını okur. Bu yüzden topic adı RASTGELE bir string olmalı
/// ("epias" gibi tahmin edilebilir DEĞİL, "epias-x7k9m2p-9q3a" gibi).
/// </summary>
public sealed class NtfyNotificationSender : INotificationSender
{
    private readonly HttpClient _httpClient;
    private readonly NtfyOptions _options;
    private readonly ILogger<NtfyNotificationSender> _logger;

    public NotificationChannel Channel => NotificationChannel.Ntfy;

    public NtfyNotificationSender(
        HttpClient httpClient,
        IOptions<NotificationOptions> options,
        ILogger<NtfyNotificationSender> logger)
    {
        _httpClient = httpClient;
        _options = options.Value.Ntfy;
        _logger = logger;
    }

    public async Task SendAsync(
        Recipient recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        // 1. Bu recipient gerçekten Ntfy istiyor mu? (defansif)
        if (!recipient.ReceivesVia(NotificationChannel.Ntfy))
        {
            _logger.LogWarning(
                "Recipient {Name} Ntfy kanalını istemiyor, gönderim atlanıyor",
                recipient.Name);
            return;
        }

        // 2. Recipient adından topic'i bul
        if (!_options.Topics.TryGetValue(recipient.Name, out var topic)
            || string.IsNullOrWhiteSpace(topic))
        {
            _logger.LogError(
                "Recipient {Name} Ntfy istiyor ama NtfyOptions.Topics'te topic tanımlı değil",
                recipient.Name);
            throw new NotificationSendException(
                NotificationChannel.Ntfy,
                recipient.Name,
                $"Recipient '{recipient.Name}' için Ntfy topic tanımlı değil");
        }

        // 3. URL ve request hazırla
        // ntfy'da URL = baseUrl + topic. Body = mesaj metni.
        var url = $"{_options.BaseUrl.TrimEnd('/')}/{topic}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };

        // 4. Opsiyonel header'lar — kullanıcı deneyimi için
        // ntfy bu header'ları okuyup bildirimi zenginleştiriyor.
        //
        // ÖNEMLİ: HTTP header'ları RFC 7230'a göre sadece ASCII karakter
        // kabul eder. Subject'te Türkçe karakter (ş, ı, vs.) veya em-dash (—)
        // varsa .NET HttpClient "Request headers must contain only ASCII"
        // exception fırlatır. ntfy bunu UTF-8 olarak da kabul eder, sadece
        // base64 encoding ister: "=?UTF-8?B?<base64>?=" formatında.
        //
        // Pratik yol: Title'ı ASCII-safe hale getiriyoruz. ASCII'ye sığarsa
        // direkt, sığmazsa base64 encode + RFC 2047 wrapper. Hem standart
        // hem de bildirimde Türkçe karakterler doğru görünür.
        request.Headers.Add("Title", EncodeHeaderValue(subject));

        // Priority: 1 (min) - 5 (max). 4 = high → ses + titreşim + ekran açılır
        request.Headers.Add("Priority", "4");

        // Tags: emoji veya kısa tag'ler. "zap" = ⚡ emoji, bildirim simgesi
        request.Headers.Add("Tags", "zap");

        // 5. Gönder
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Ntfy {Status}: {Body}",
                    (int)response.StatusCode, errorBody);
                throw new NotificationSendException(
                    NotificationChannel.Ntfy,
                    recipient.Name,
                    $"Ntfy HTTP {(int)response.StatusCode}: {errorBody}");
            }

            _logger.LogInformation(
                "Ntfy bildirim gönderildi: {Recipient} (topic: {Topic})",
                recipient.Name, topic);
        }
        catch (Exception ex) when (ex is not NotificationSendException)
        {
            // Network, DNS, timeout, vs.
            _logger.LogError(ex,
                "Ntfy gönderilemedi: {Recipient}",
                recipient.Name);
            throw new NotificationSendException(
                NotificationChannel.Ntfy,
                recipient.Name,
                $"Ntfy gönderim hatası: {ex.Message}",
                ex);
        }
    }
    /// <summary>
    /// HTTP header'ında güvenli kullanılacak string üretir. Sadece ASCII
    /// karakter varsa olduğu gibi döner; non-ASCII varsa RFC 2047
    /// encoded-word formatına çevirir (=?UTF-8?B?...?=) — ntfy ve modern
    /// HTTP client'lar bunu çözüp UTF-8 olarak gösterir.
    /// </summary>
    private static string EncodeHeaderValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        // Tüm karakterler ASCII (0-127) mı?
        bool isAscii = true;
        foreach (var c in value)
        {
            if (c > 127)
            {
                isAscii = false;
                break;
            }
        }

        if (isAscii) return value;

        // Non-ASCII varsa: UTF-8 byte'larını base64'e çevir, RFC 2047 ile sar
        var utf8Bytes = Encoding.UTF8.GetBytes(value);
        var base64 = Convert.ToBase64String(utf8Bytes);
        return $"=?UTF-8?B?{base64}?=";
    }
}