using EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EpiasPriceNotifier.Infrastructure.Notifications;

/// <summary>
/// IRecipientProvider'ın config-based implementasyonu.
///
/// NotificationOptions.Recipients dictionary'sindeki string anahtarları
/// (örn. "User1") ve string-array değerleri (örn. ["Telegram", "Email"])
/// Domain'in Recipient value object'lerine çevirir.
///
/// Bu dönüşümü daha önce Worker/Program.cs içindeki /test/notify
/// endpoint'inde yapıyorduk; şimdi düzgün bir yerde, doğru abstraction'ın
/// arkasında. Handler artık config detaylarını bilmiyor.
/// </summary>
internal sealed class RecipientProvider : IRecipientProvider
{
    private readonly NotificationOptions _options;
    private readonly ILogger<RecipientProvider> _logger;

    public RecipientProvider(
        IOptions<NotificationOptions> options,
        ILogger<RecipientProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IEnumerable<Recipient> GetRecipients()
    {
        foreach (var kvp in _options.Recipients)
        {
            var name = kvp.Key;
            var channelStrings = kvp.Value;

            // String -> NotificationChannel enum dönüşümü
            // Geçersiz değer varsa o recipient'ı atlıyoruz, akış devam etsin
            var channels = new List<NotificationChannel>();
            foreach (var s in channelStrings)
            {
                if (Enum.TryParse<NotificationChannel>(s, ignoreCase: true, out var channel))
                {
                    channels.Add(channel);
                }
                else
                {
                    _logger.LogWarning(
                        "Recipient {Name} için bilinmeyen kanal '{Channel}' yok sayılıyor",
                        name, s);
                }
            }

            // En az bir geçerli kanal var mı?
            if (channels.Count == 0)
            {
                _logger.LogWarning(
                    "Recipient {Name} için geçerli kanal yok, atlanıyor", name);
                continue;
            }

            // Recipient constructor invariant'ı (name boş olamaz, vs.)
            // hala koruyucu — buraya bozuk veri geçemez
            yield return new Recipient(name, channels);
        }
    }
}