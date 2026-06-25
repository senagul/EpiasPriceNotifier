using EpiasPriceNotifier.Domain.Enums;

namespace EpiasPriceNotifier.Application.Common.Exceptions;

/// <summary>
/// Bildirim gönderiminde hata olduğunu ifade eden exception.
/// GlobalExceptionHandler bunu yakaladığında HTTP 503 Service Unavailable
/// döndürür — "biz cevap veremiyoruz, dış sistem patladı".
///
/// Hangi kanalın patladığını property olarak taşır — log'da ve
/// ProblemDetails response'unda bu bilgi çok değerli ("Telegram down,
/// Email çalışıyor" gibi).
///
/// Dispatcher bu exception'ı YAKALAR, log'lar, ama PİPELINE'I BOZMAZ —
/// diğer kanallara devam eder. Bu sayede bir kanal patlasa Recipient
/// diğerlerinden yine de mesaj alır.
/// </summary>
public sealed class NotificationSendException : Exception
{
    /// <summary>Hangi kanal patladı (Telegram, Email, Ntfy).</summary>
    public NotificationChannel Channel { get; }

    /// <summary>Hedef recipient'ın adı — log'larda hangi gönderim patladığı bilgisi.</summary>
    public string RecipientName { get; }

    public NotificationSendException(
        NotificationChannel channel,
        string recipientName,
        string message)
        : base(message)
    {
        Channel = channel;
        RecipientName = recipientName;
    }

    public NotificationSendException(
        NotificationChannel channel,
        string recipientName,
        string message,
        Exception inner)
        : base(message, inner)
    {
        Channel = channel;
        RecipientName = recipientName;
    }
}