namespace EpiasPriceNotifier.Application.Common.Exceptions;

/// <summary>
/// Bir kayıt veya kaynağın bulunamadığını ifade eden exception.
/// GlobalExceptionHandler bunu yakaladığında HTTP 404 Not Found döndürür.
///
/// Örnek kullanım:
///   throw new NotFoundException(nameof(DailyPriceSchedule), date);
///
/// Niye Application katmanında?
/// Domain "kayıt bulunamadı" gibi infrastructure-vari bir kavramı bilmek
/// zorunda değil. Application use case'leri "şu tarihteki schedule yok"
/// gibi durumları bu exception'la ifade eder. GlobalExceptionHandler ise
/// Worker'da, HTTP'ye nasıl çevrileceğini bilen tek yer.
///
/// Niye sealed?
/// Bu exception'ın subclass'lanmaya ihtiyacı yok — anlamı zaten kesin
/// ("bulunamadı"). Subclass'ı engellemek hem performans (devirtualization)
/// hem semantic netlik için artı.
/// </summary>
public sealed class NotFoundException : Exception
{
    /// <summary>
    /// Bulunamayan kaydı, anahtarıyla birlikte mesajlayan constructor.
    /// </summary>
    /// <param name="entityName">Aranan tipin adı, örn. "DailyPriceSchedule".</param>
    /// <param name="key">Aranan anahtar değer, örn. tarih veya ID.</param>
    public NotFoundException(string entityName, object key)
        : base($"{entityName} bulunamadı (key: {key})")
    {
    }

    /// <summary>
    /// Doğrudan custom mesajla fırlatmak için.
    /// </summary>
    public NotFoundException(string message) : base(message)
    {
    }
}