using System.Diagnostics.Metrics;
using EpiasPriceNotifier.Domain.Enums;

namespace EpiasPriceNotifier.Infrastructure.Observability;

/// <summary>
/// Uygulamaya özel OpenTelemetry metric'leri.
///
/// Şu an Infrastructure katmanında çağrılan metric'leri tutuyor (dispatcher).
/// Application katmanından metric kaydı gerekirse bir IObservabilityMetrics
/// abstraction'ı eklenir, EpiasMetrics onu implement eder — Application'ın
/// Infrastructure'a leak'lemesini önlemek için. Şu an handler metric çağırmadığı
/// için bu abstraction PR'ı ertelendi.
/// </summary>
public sealed class EpiasMetrics
{
    public const string MeterName = "EpiasPriceNotifier.Notifications";

    private readonly Meter _meter;
    private readonly Counter<long> _dispatchedCounter;
    private readonly Counter<long> _failedCounter;
    private readonly Histogram<double> _dispatchDurationHistogram;

    public EpiasMetrics()
    {
        _meter = new Meter(MeterName, version: "1.0.0");

        _dispatchedCounter = _meter.CreateCounter<long>(name: "epias.notifications.dispatched", unit: "{notification}", description: "Toplam başarıyla gönderilen bildirim sayısı (recipient × channel)");

        _failedCounter = _meter.CreateCounter<long>(name: "epias.notifications.failed", unit: "{notification}", description: "Toplam başarısız bildirim sayısı");

        _dispatchDurationHistogram = _meter.CreateHistogram<double>(name: "epias.dispatch.duration", unit: "s", description: "Bildirim dispatch wall-clock süresi (paralel toplam değil)");
    }

    public void RecordDispatched(NotificationChannel channel, string recipientName) =>
        _dispatchedCounter.Add(1, new KeyValuePair<string, object?>("channel", channel.ToString()), new KeyValuePair<string, object?>("recipient", recipientName));

    public void RecordFailed(NotificationChannel channel, string recipientName, string errorType) =>
        _failedCounter.Add(1, new KeyValuePair<string, object?>("channel", channel.ToString()), new KeyValuePair<string, object?>("recipient", recipientName), new KeyValuePair<string, object?>("error_type", errorType));

    public void RecordDispatchDuration(double seconds) => _dispatchDurationHistogram.Record(seconds);
}