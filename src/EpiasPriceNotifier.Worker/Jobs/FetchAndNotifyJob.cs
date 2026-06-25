using EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;
using MediatR;
using Quartz;

namespace EpiasPriceNotifier.Worker.Jobs;

/// <summary>
/// Her gün belirlenen saatte (cron) ertesi günün PTF'ini çekip
/// ucuz saat bildirimi atan Quartz job'ı.
///
/// Job'ın tek sorumluluğu: doğru tarihi hesaplayıp MediatR command'ını
/// tetiklemek. Asıl iş Application katmanının handler'ında — job
/// orchestration'a değil, scheduling'e ait.
///
/// Niye `[DisallowConcurrentExecution]`?
/// Aynı job hala çalışırken yeni tetikleme gelse (örn. cron yeniden
/// tetikledi veya manuel trigger), Quartz default'ta paralel başlatır.
/// Bizim use case için bu istenmeyen: aynı gün iki kez bildirim atmak
/// kullanıcı için kötü UX. Attribute Quartz'a "bu job çalışıyorsa
/// yeni instance başlatma, sıraya da koyma — direkt atla" der.
///
/// Niye `[PersistJobDataAfterExecution]` YOK?
/// In-memory Quartz kullanıyoruz, job state persist etmiyoruz.
/// İleride SQL Server JobStore'a geçersek bu attribute lazım olabilir.
/// </summary>
[DisallowConcurrentExecution]
public sealed class FetchAndNotifyJob : IJob
{
    private readonly IMediator _mediator;
    private readonly ILogger<FetchAndNotifyJob> _logger;

    public FetchAndNotifyJob(
        IMediator mediator,
        ILogger<FetchAndNotifyJob> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        // Quartz job'larda CancellationToken context üzerinden geliyor.
        // Server shutdown sırasında bu token cancel edilir — job da
        // graceful kapanmalı, yarım kalmış HTTP istekleri iptal olmalı.
        var ct = context.CancellationToken;

        // EPİAŞ her gün ~14:00 civarı YARININ fiyatlarını yayımlar.
        // Cron 15:00'te tetiklenecek (config'ten), yani biz çağırırken
        // bugünün veya yarının PTF'i mevcut olmalı. YARIN'ı istiyoruz
        // çünkü kullanıcı "yarın ne yapsam" sorusuna cevap arıyor.
       var tomorrow = DateOnly.FromDateTime(DateTime.Today.AddDays(1));



        // TEST: Şimdilik dünün verisiyle test ediyoruz çünkü yarın için EPİAŞ
        // verisi henüz açıklanmamış olabilir. Production'a almadan önce
        // AddDays(+1)'e çevireceğiz.
       // var tomorrow = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));

        _logger.LogInformation(
            "FetchAndNotifyJob tetiklendi. Hedef tarih: {Date}, fire time: {FireTime}",
            tomorrow, context.FireTimeUtc);

        try
        {
            await _mediator.Send(new FetchAndNotifyCheapHoursCommand(tomorrow), ct);

            _logger.LogInformation(
                "FetchAndNotifyJob başarıyla tamamlandı. Bir sonraki tetikleme: {NextFire}",
                context.NextFireTimeUtc);
        }
        catch (Exception ex)
        {
            // Job içinde exception'ı yakalayıp yutmak kasıtlı.
            // Quartz default'ta exception'ı yakalar ve job'ı "failed" işaretler;
            // bu durumda misfire policy'ye göre tekrar denenebilir.
            // Biz burada loglayıp Quartz'a bırakıyoruz; kararı o veriyor.
            _logger.LogError(ex,
                "FetchAndNotifyJob başarısız oldu. Hedef tarih: {Date}",
                tomorrow);

            // Quartz misfire'lara karar verirken JobExecutionException'a bakar.
            // refireImmediately: false → hemen tekrar deneme; bir sonraki cron
            // tetiklemesini bekle. EPİAŞ yarım saat down olsa, 24 saat sonra
            // tekrar denemek mantıklı çünkü ertesi gün için yeniden veri lazım.
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}