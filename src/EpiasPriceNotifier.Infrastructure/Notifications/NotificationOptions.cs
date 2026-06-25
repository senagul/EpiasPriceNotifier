namespace EpiasPriceNotifier.Infrastructure.Notifications;

/// <summary>
/// Bildirim altyapısının tüm config'i. appsettings.json'daki
/// "Notifications" bölümüne map'lenir. Hassas alanlar (token, şifre,
/// chat ID'ler) user-secrets'tan gelir.
///
/// Yapı bilinçli olarak iç içe:
/// - Recipients: kim, hangi kanalları istiyor
/// - Telegram/Email/Ntfy: kanal-özel ayarlar + per-recipient mapping
///
/// Niye recipient adı dictionary key'i?
/// "Anne" gibi okunabilir bir anahtar config dosyasını insan-okur yapıyor.
/// JSON'a baktığında "Anne Telegram alıyor mu" hemen görüyorsun.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>appsettings.json'daki bölüm adı.</summary>
    public const string SectionName = "Notifications";

    /// <summary>
    /// Kimler bildirim alacak ve hangi kanallardan.
    /// Örnek: { "Sena": ["Telegram", "Email"], "Anne": ["Email"] }
    ///
    /// Niye string-string[] dictionary?
    /// appsettings.json'dan binding çok kolay; enum'lara dispatcher'da
    /// dönüştüreceğiz. JSON'da enum string olarak yazılır, kullanıcı dostu.
    /// </summary>
    /// 
    public decimal ThresholdTryPerKwh { get; init; } = 0.30m;
    public Dictionary<string, string[]> Recipients { get; init; } = new();

    public TelegramOptions Telegram { get; init; } = new();
    public EmailOptions Email { get; init; } = new();
    public NtfyOptions Ntfy { get; init; } = new();
}

/// <summary>Telegram Bot API ayarları + recipient → chatId mapping.</summary>
public sealed class TelegramOptions
{
    /// <summary>BotFather'dan alınan bot token. User-secrets'ta saklanır.</summary>
    public string BotToken { get; init; } = string.Empty;

    /// <summary>
    /// Recipient adı → Telegram chat ID. Her kullanıcının chat ID'si farklı.
    /// Örnek: { "Sena": 123456789, "Anne": 987654321 }
    /// </summary>
    public Dictionary<string, long> ChatIds { get; init; } = new();
}

/// <summary>SMTP ayarları + recipient → email adresi mapping.</summary>
public sealed class EmailOptions
{
    /// <summary>SMTP host (örn. smtp.gmail.com).</summary>
    public string SmtpHost { get; init; } = "smtp.gmail.com";

    /// <summary>SMTP port (Gmail için 587 — STARTTLS).</summary>
    public int SmtpPort { get; init; } = 587;

    /// <summary>Gönderici e-posta (kendi Gmail adresin).</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Gmail App Password (16 haneli, user-secrets'ta).</summary>
    public string AppPassword { get; init; } = string.Empty;

    /// <summary>
    /// Recipient adı → email adresi.
    /// Örnek: { "Sena": "senagul@gmail.com" }
    /// </summary>
    public Dictionary<string, string> Addresses { get; init; } = new();
}

/// <summary>ntfy.sh ayarları + recipient → topic mapping.</summary>
public sealed class NtfyOptions
{
    /// <summary>ntfy server base URL. Default public ntfy.sh.</summary>
    public string BaseUrl { get; init; } = "https://ntfy.sh";

    /// <summary>
    /// Recipient adı → topic name.
    /// Örnek: { "Sena": "epias-sena-x7k1" }
    /// Senin gibi sadece bir kişi ntfy kullanıyorsa burada bir tane olur.
    /// </summary>
    public Dictionary<string, string> Topics { get; init; } = new();
}