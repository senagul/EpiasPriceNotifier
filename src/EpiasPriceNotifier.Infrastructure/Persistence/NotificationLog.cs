namespace EpiasPriceNotifier.Infrastructure.Persistence;

/// <summary>
/// Bir günün PTF'i için bildirim gönderilip gönderilmediğini saklayan kayıt.
/// Idempotency için kullanılır: aynı tarih için ikinci kez kayıt eklenmesi
/// engellenir (PtfDate üzerinde UNIQUE INDEX).
///
/// Niye Domain'de değil Infrastructure'da?
/// NotificationLog Application'ın bir "iş kuralı" değil — sadece teknik bir
/// idempotency mekanizması. Bu yüzden Infrastructure katmanında, persistence
/// detayı olarak yer alıyor. Domain'de "kaç gün bildirim attık" gibi bir iş
/// kuralı olsaydı domain entity'sine taşırdık.
///
/// Niye EF entity'leri sealed değil?
/// EF Core proxy'ler oluşturuyor (lazy loading, change tracking) — sealed
/// class ile proxy yaratamaz. Bu yüzden EF entity'leri genellikle sealed
/// değildir. Domain value object'lerimiz sealed kalıyor; EF entity'leri
/// pragmatik istisna.
/// </summary>
public class NotificationLog
{
    /// <summary>Auto-increment primary key.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Bildirimin hangi gün için gönderildiği. PTF tarihi.
    /// Idempotency anahtarı: bu kolonda UNIQUE INDEX var.
    /// </summary>
    public DateOnly PtfDate { get; set; }

    /// <summary>
    /// Gönderim zamanı (UTC). Audit log için.
    /// Schedule edilen 15:00 ile gerçekleşen zaman arasındaki farkı görmek
    /// için DateTimeOffset yerine UTC yeterli.
    /// </summary>
    public DateTime SentAt { get; set; }

    /// <summary>
    /// Kaç recipient'a gönderildi. Operasyonel debug için.
    /// </summary>
    public int RecipientCount { get; set; }

    /// <summary>
    /// Gönderilen mesajın subject'i (kısa). Hangi tip bildirim olduğunu
    /// hatırlamak için (örn. "BEDAVA elektrik saatleri var (21 Haz)").
    /// </summary>
    public string Subject { get; set; } = string.Empty;
}