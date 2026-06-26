namespace EpiasPriceNotifier.Application.Abstractions;

/// <summary>
/// Bildirim gönderim kayıtları için repository port.
///
/// Application katmanı NotificationLog entity'sini bilmemeli — bu yüzden
/// methodlar primitive tipler kullanıyor (DateOnly, int, string).
/// Implementation Infrastructure'da entity'ye map'leyecek.
///
/// İki sorumluluk:
///   - HasSentForDateAsync: idempotency check (aynı gün gönderildi mi?)
///   - RecordSentAsync: başarılı gönderimin kaydını tut
///
/// Niye iki ayrı method? Tek bir "GetOrCreate" daha mı temiz olurdu?
/// İki sebepten ötürü hayır:
///   1) Handler'da iki ayrı niyet ifade ediliyor — "kontrol et" ve "kaydet".
///      Method isimleri okumayı kolaylaştırıyor.
///   2) Idempotency check gönderim ÖNCESİ, kayıt gönderim SONRASI. Aralarındaki
///      dispatcher.SendAsync çağrısı (yan etki) tek bir transactional işleme
///      sıkıştırılamaz. İki ayrı method gerçeği yansıtıyor.
/// </summary>
public interface INotificationLogRepository
{
    /// <summary>
    /// Verilen PTF tarihi için daha önce başarılı bildirim gönderilmiş mi?
    /// </summary>
    Task<bool> HasSentForDateAsync(
        DateOnly ptfDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Başarılı bir bildirim gönderiminin kaydını ekler.
    /// Aynı tarih için ikinci kayıt eklenirse UNIQUE constraint patlatır
    /// (Infrastructure katmanı bu exception'ı NotificationLogException'a
    /// çevirebilir veya yutabilir — pratikte handler "zaten var" durumunu
    /// HasSentForDateAsync ile önceden engelliyor).
    /// </summary>
    Task RecordSentAsync(
        DateOnly ptfDate,
        int recipientCount,
        string subject,
        CancellationToken cancellationToken = default);
}