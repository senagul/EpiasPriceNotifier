using MediatR;

namespace EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;

/// <summary>
/// Belirli bir gün için EPİAŞ PTF'lerini çekip, ucuz saatleri bulup,
/// configured tüm recipient'lara bildirim gönderir.
///
/// CQRS pattern:
/// - Command (yazma/side-effect taşıyan operasyon) — sonuç sadece "yapıldı mı"
/// - Query (sorgu, readonly) ise IRequest&lt;TResult&gt; ile yazılır
///
/// Bu command bir Unit (void karşılığı) döner — anlamlı bir return value yok,
/// bildirim gönderildi demek. Hata olursa exception fırlatır.
///
/// </summary>
/// <param name="Date">PTF istenen gün. Genellikle yarın (Quartz job ertesi günü hesaplatır).</param>
public sealed record FetchAndNotifyCheapHoursCommand(DateOnly Date) : IRequest;