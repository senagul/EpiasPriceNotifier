using EpiasPriceNotifier.Application.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace EpiasPriceNotifier.Worker.ExceptionHandlers;

/// <summary>
/// Uygulamadaki TÜM exception'ları yakalayan tek merkezi sınıf.
/// .NET 8 ile gelen IExceptionHandler interface'ini implement eder.
///
/// Felsefe: kod akışında "try/catch" yığını olmaz. Use case'ler,
/// endpoint handler'ları, validator'lar — herkes exception'ı serbestçe
/// fırlatır. Bu sınıf yakalar, switch ile tipine göre HTTP status code'a
/// map'ler, RFC 7807 ProblemDetails formatında response yazar.
///
/// Niye Worker projesinde?
/// IExceptionHandler ASP.NET Core'a özgüdür — HTTP context görür.
/// Worker projemiz Minimal API + BackgroundService hibridi olduğu için
/// hem HTTP endpoint'lerinde hem (gerekirse) job'larda paylaşılır mantık.
/// Application/Infrastructure HTTP framework'ünü bilmek zorunda değil.
///
/// Niye senagul/MinimalAPIAndGrpc pattern'ı?
/// Tek bir class, switch expression, sealed — minimum sürpriz.
/// "5 katmanlı savunma hattı" yerine net bir sorumluluk: HTTP isteğinde
/// patlayan exception'ı HTTP cevabına çevir.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Middleware'in çağırdığı method. true dönmek = "bu exception ele alındı,
    /// başka handler'a gerek yok". false dönmek = "ben başaramadım, sıradakine".
    /// Tek handler'ımız var, her zaman true.
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // 1. Exception tipini status code ve insan-okur başlığa çevir
        var (statusCode, title) = MapException(exception);

        // 2. Logla — 4xx warning, 5xx error seviyesinde
        LogException(exception, statusCode);

        // 3. RFC 7807 ProblemDetails formatında response yaz
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Type = exception.GetType().Name,
            Title = title,
            Detail = exception.Message,
            Instance = httpContext.Request.Path
        };

        // ValidationException özel durumu: alan bazlı hata listesini
        // ProblemDetails.Extensions'a "errors" anahtarıyla koy.
        // Microsoft'un model validation convention'ı bu format.
        if (exception is ValidationException validationEx)
        {
            problemDetails.Extensions["errors"] = validationEx.Errors;
        }

        // EpiasIntegrationException özel durumu: upstream status code'unu
        // response'a debug kolaylığı için ekle.
        if (exception is EpiasIntegrationException epiasEx && epiasEx.StatusCode.HasValue)
        {
            problemDetails.Extensions["epiasStatusCode"] = epiasEx.StatusCode.Value;
        }

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json";
        // WriteAsJsonAsync content-type'ı "application/json" olarak override
        // ediyor — RFC 7807 standardı "application/problem+json" istiyor.
        // Manuel serialize + WriteAsync ile content-type'ı koruyoruz.
        var json = JsonSerializer.Serialize(problemDetails, JsonOptions);
        await httpContext.Response.WriteAsync(json, cancellationToken);

        // true = handled. Pipeline'da sıradaki handler'a (varsa) geçme.
        return true;
    }
    /// <summary>
    /// camelCase property isimleri (frontend convention) + null property'leri atla.
    /// Bir kez yarat, tekrar kullan — JsonSerializerOptions her instance'ta
    /// internal cache kurar; sık yaratmak performans katiline dönüşür.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Exception → (HTTP status, başlık) eşleştirmesi.
    /// Switch expression: yeni exception tipi eklemek istersen tek satır
    /// "arm" eklersin. Maintainability burada en yüksek seviyede.
    ///
    /// Default arm (_) çok önemli: bilinmeyen exception'ları 500'e
    /// çeviriyor. Atlayamayız — yoksa fırlatılan unknown tip middleware'de
    /// patlardı.
    /// </summary>
    private static (int StatusCode, string Title) MapException(Exception exception) =>
        exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Kayıt bulunamadı"),
            ValidationException => (StatusCodes.Status400BadRequest, "Geçersiz istek"),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Yetkisiz erişim"),
            EpiasIntegrationException ex => (StatusCodes.Status502BadGateway, $"EPİAŞ entegrasyon hatası ({ex.StatusCode?.ToString() ?? "bilinmeyen"})"),
            TaskCanceledException => (StatusCodes.Status408RequestTimeout, "İstek zaman aşımına uğradı"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Geçersiz argüman"),
            _ => (StatusCodes.Status500InternalServerError, "Beklenmedik bir hata oluştu")
        };

    /// <summary>
    /// Status koduna göre log seviyesi seç.
    /// 4xx → client hatası, Warning yeter (sistem bozulmadı).
    /// 5xx → server/integration hatası, Error seviyesinde log + alarm.
    ///
    /// Burada Telegram'a kritik alarm gönderme mantığını da koyabilirdik.
    /// Şimdilik sadece log; bildirim PR'ında bunu da entegre edeceğiz.
    /// </summary>
    private void LogException(Exception exception, int statusCode)
    {
        if (statusCode >= 500)
        {
            _logger.LogError(exception,
                "Unhandled exception ({StatusCode}): {Message}",
                statusCode, exception.Message);
        }
        else
        {
            _logger.LogWarning(exception,
                "Handled exception ({StatusCode}): {Message}",
                statusCode, exception.Message);
        }
    }
}