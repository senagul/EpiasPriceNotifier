using EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;
using EpiasPriceNotifier.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace EpiasPriceNotifier.Infrastructure.Notifications;

/// <summary>
/// IPriceThresholdProvider'ın config-based implementasyonu.
///
/// Config'teki NotificationOptions.Threshold değerini PriceThreshold value
/// object'ine çevirip handler'a sunar.
///
/// Niye bu sınıf var?
/// Handler doğrudan IOptions&lt;NotificationOptions&gt; inject edebilirdi
/// ama o zaman:
///   - Application katmanı Infrastructure'ın config tipini bilmek zorunda kalırdı
///   - Test'te config setup'ı karmaşıklaşırdı
///   - Yarın threshold DB'den gelse handler kodu değişirdi
///
/// Bu küçük "adapter" sınıfı domain'i Microsoft DI'ın config tiplerinden izole ediyor.
/// </summary>
internal sealed class PriceThresholdProvider : IPriceThresholdProvider
{
    private readonly NotificationOptions _options;

    public PriceThresholdProvider(IOptions<NotificationOptions> options)
    {
        _options = options.Value;
    }

    public PriceThreshold GetThreshold()
    {
        // Config'te threshold tanımsızsa default 0.30 TL/kWh
        // (mantıklı bir varsayılan — son kullanıcı için anlamlı bir eşik).
        var amount = _options.ThresholdTryPerKwh > 0m
            ? _options.ThresholdTryPerKwh
            : 0.30m;

        return PriceThreshold.FromTryPerKwh(amount);
    }
}