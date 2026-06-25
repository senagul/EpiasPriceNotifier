using EpiasPriceNotifier.Domain.Enums;

namespace EpiasPriceNotifier.Domain.ValueObjects;

/// <summary>
/// Bildirim alıcısı — bir isim ve hangi kanallardan bildirim istediği.
///
/// Kanal bilgisi RECIPIENT'a ait, sender'a değil.
/// Sender'lar sadece "ben Telegram'ım, bana gelen Recipient'lara mesaj atarım"
/// derler — kim olduğunu, başka kanal kullanıp kullanmadığını bilmezler.
///
/// Niye sealed record?
/// - Immutable: Recipient bir kez yaratılınca değişmez
/// - Value equality: aynı isim + aynı kanallar = aynı recipient (test için faydalı)
/// - sealed: inheritance kapalı, davranış net
/// </summary>
public sealed record Recipient
{
    /// <summary>
    /// Alıcının görünen adı. Sender'lar bunu kullanarak kendi mapping'lerinden
    /// (chat ID, email adresi, ntfy topic) doğru hedefi bulur.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Bu alıcıya hangi kanallardan bildirim gönderileceği.
    /// IReadOnlyList: dışarıdan eklenip çıkarılamaz (immutability).
    /// </summary>
    public IReadOnlyList<NotificationChannel> Channels { get; }

    public Recipient(string name, IEnumerable<NotificationChannel> channels)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "Recipient adı boş olamaz",
                nameof(name));

        ArgumentNullException.ThrowIfNull(channels);

        // Liste'yi materialize edip distinct yapıyoruz —
        // yanlışlıkla aynı kanalı iki kere koyarsa kullanıcı, biz temizliyoruz.
        var distinct = channels.Distinct().ToList();

        if (distinct.Count == 0)
            throw new ArgumentException(
                $"Recipient '{name}' için en az bir kanal seçilmeli",
                nameof(channels));

        Name = name;
        Channels = distinct.AsReadOnly();
    }

    /// <summary>
    /// Bu recipient verilen kanaldan bildirim alıyor mu?
    /// Sender'lar dispatcher'dan gelmeden önce sorgulayabilir.
    /// </summary>
    public bool ReceivesVia(NotificationChannel channel) =>
        Channels.Contains(channel);

    public override string ToString() =>
        $"{Name} ({string.Join(", ", Channels)})";
}