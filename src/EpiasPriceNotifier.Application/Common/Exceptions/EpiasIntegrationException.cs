namespace EpiasPriceNotifier.Application.Common.Exceptions;

/// <summary>
/// EPİAŞ entegrasyonunda hata olduğunu ifade eden exception.
/// GlobalExceptionHandler bunu yakaladığında HTTP 502 Bad Gateway döndürür —
/// "biz değil, bağlı olduğumuz sistem patladı" sinyali.
///
/// Örnek kullanım:
///   throw new EpiasIntegrationException("TGT alınamadı", statusCode: 500, inner: ex);
///   throw new EpiasIntegrationException("MCP timeout");
///
/// Niye Application'da, Infrastructure'da değil?
/// Exception tipini "fırlatıldığı yer" değil "anlamı" belirler. EPİAŞ
/// entegrasyon hatası bir Application konsepti — use case'leri bu exception'ı
/// yakalayıp farklı stratejiler izleyebilir (retry, fallback, vs.).
/// Infrastructure sadece fırlatır.
///
/// Niye sadece string mesaj değil de StatusCode da var?
/// Log'da ve ProblemDetails response'unda "hangi HTTP koduyla patladı"
/// bilgisi çok değerli. Genel "504" mü, auth "401" mi, server "500" mü?
/// Operatör için kritik fark.
/// </summary>
public sealed class EpiasIntegrationException : Exception
{
    /// <summary>
    /// EPİAŞ'tan dönen HTTP status code (varsa). Network hatası,
    /// timeout vs. gibi durumlarda null olabilir.
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// EPİAŞ entegrasyon hatası — sadece mesajla.
    /// </summary>
    public EpiasIntegrationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// EPİAŞ entegrasyon hatası — HTTP status code'u ile.
    /// </summary>
    public EpiasIntegrationException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// EPİAŞ entegrasyon hatası — inner exception ile zincirleme.
    /// HttpRequestException, TaskCanceledException vs. sarmak için.
    /// Inner exception sayesinde stack trace tam olarak görünür.
    /// </summary>
    public EpiasIntegrationException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>
    /// EPİAŞ entegrasyon hatası — hem status code hem inner exception ile.
    /// En kapsamlı versiyonu.
    /// </summary>
    public EpiasIntegrationException(string message, int statusCode, Exception inner)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}