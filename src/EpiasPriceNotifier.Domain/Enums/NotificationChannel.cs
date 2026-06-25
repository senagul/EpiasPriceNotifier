namespace EpiasPriceNotifier.Domain.Enums;

/// <summary>
/// Bildirim kanalları. Bir Recipient bir veya daha fazlasını seçebilir.

/// </summary>
public enum NotificationChannel
{
    Telegram = 1,
    Email = 2,
    Ntfy = 3
}