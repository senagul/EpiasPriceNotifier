namespace EpiasPriceNotifier.Application.Common.Exceptions;

/// <summary>
/// Validation hatalarını ifade eden exception.
/// GlobalExceptionHandler bunu yakaladığında HTTP 400 Bad Request +
/// ProblemDetails içinde alan bazlı hata listesi döndürür.
///
/// Örnek kullanım:
///   throw new ValidationException(new Dictionary&lt;string, string[]&gt;
///   {
///       ["Date"]      = new[] { "Geçerli bir tarih girin (yyyy-MM-dd)" },
///       ["Threshold"] = new[] { "Eşik 0'dan büyük olmalı" }
///   });
///
/// Niye Dictionary&lt;string, string[]&gt;?
/// FluentValidation'ın çıktısı zaten bu şekildedir — "alan adı → o alana ait
/// hata listesi". Aynı alan birden fazla kuralı geçemeyebilir (örn. hem boş
/// hem geçersiz format). FluentValidation entegrasyonu için en doğal şekil.
/// </summary>
public sealed class ValidationException : Exception
{
    /// <summary>
    /// Alan adı → o alana ait hata mesajları sözlüğü.
    /// GlobalExceptionHandler bu sözlüğü ProblemDetails'in extensions kısmına
    /// "errors" anahtarıyla koyar (RFC 7807 + Microsoft validation convention).
    /// </summary>
    public IDictionary<string, string[]> Errors { get; }

    /// <summary>
    /// Boş validation exception — runtime'da Errors koleksiyonunu doldurmak için.
    /// Default mesaj "Validation hatası".
    /// </summary>
    public ValidationException() : base("Validation hatası")
    {
        Errors = new Dictionary<string, string[]>();
    }

    /// <summary>
    /// Alan-hata sözlüğü ile validation exception.
    /// FluentValidation entegrasyonunda en sık kullanılan constructor.
    /// </summary>
    public ValidationException(IDictionary<string, string[]> errors)
        : base("Validation hatası")
    {
        Errors = errors ?? new Dictionary<string, string[]>();
    }

    /// <summary>
    /// Tek bir alan-mesaj çifti ile hızlı validation exception.
    /// Manuel kontrol durumlarında pratik.
    /// </summary>
    public ValidationException(string field, string error)
        : base("Validation hatası")
    {
        Errors = new Dictionary<string, string[]>
        {
            [field] = new[] { error }
        };
    }
}