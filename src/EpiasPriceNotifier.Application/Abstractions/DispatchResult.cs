namespace EpiasPriceNotifier.Application.Abstractions;

/// <summary>
/// NotificationDispatcher.SendAsync çağrısının özet sonucu.
///
/// Handler bu sonuca bakarak post-dispatch aksiyonlarına (örn. idempotency
/// kaydı) karar veriyor. "Tüm kanallar patladı" senaryosunda kayıt atılmasın
/// diye SuccessCount > 0 kontrolü yapılıyor.
///
/// Niye sadece count, detay yok?
/// Failure detayları (hangi kanal, hangi recipient, hangi hata) zaten
/// Dispatcher tarafından loglanıyor. Handler'ın karar mantığı için iki
/// sayı yeterli — KISS. İleride detay lazım olursa property eklemek
/// geriye dönük uyumlu (record özelliği).
///
/// Niye sealed record?
/// - Immutable: yarat-dön, modify yok
/// - Value semantic: equality testlerinde otomatik karşılaştırma
/// - sealed: inheritance kapalı, davranış net
/// </summary>
/// <param name="SuccessCount">Başarıyla gönderilen (recipient × kanal) çiftleri.</param>
/// <param name="FailureCount">Başarısız olan (recipient × kanal) çiftleri.</param>
public sealed record DispatchResult(int SuccessCount, int FailureCount)
{
    /// <summary>Toplam gönderim girişimi (success + failure).</summary>
    public int TotalAttempts => SuccessCount + FailureCount;

    /// <summary>
    /// En az bir başarılı gönderim oldu mu?
    /// Handler "kayıt at" kararını bu property üzerinden veriyor.
    /// </summary>
    public bool HasAnySuccess => SuccessCount > 0;

    /// <summary>
    /// Hiçbir gönderim denenmedi (empty recipient list veya recipient'lar
    /// kayıtlı olmayan kanalları istemiş).
    /// </summary>
    public bool NoAttempts => TotalAttempts == 0;

    /// <summary>Hepsi başarılı mı?</summary>
    public bool AllSucceeded => FailureCount == 0 && SuccessCount > 0;

    /// <summary>Boş sonuç (hiç gönderim yapılmadı).</summary>
    public static DispatchResult Empty => new(0, 0);
}