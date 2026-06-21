namespace EpiasPriceNotifier.Domain.ValueObjects;

/// <summary>
/// Ardışık ucuz saatlerin oluşturduğu zaman aralığı.
/// Örneğin 02:00, 03:00, 04:00 ucuzsa → CheapWindow(02:00, 05:00, 3 saat).
/// End exclusive: 02:00–05:00 → 02, 03, 04 saatleri demektir.
/// </summary>
public sealed record CheapWindow
{
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public int HourCount { get; }
    public decimal AveragePriceTryPerKwh { get; }
    public decimal MinPriceTryPerKwh { get; }

    public CheapWindow(IReadOnlyList<HourlyPrice> consecutiveHours)
    {
        ArgumentNullException.ThrowIfNull(consecutiveHours);

        if (consecutiveHours.Count == 0)
            throw new ArgumentException(
                "CheapWindow en az 1 saat içermeli",
                nameof(consecutiveHours));

        for (var i = 1; i < consecutiveHours.Count; i++)
        {
            var expected = consecutiveHours[i - 1].Hour.AddHours(1);
            if (consecutiveHours[i].Hour != expected)
                throw new ArgumentException(
                    $"Saatler ardışık olmalı, kırılma: " +
                    $"{consecutiveHours[i - 1].Hour:HH:mm} → {consecutiveHours[i].Hour:HH:mm}",
                    nameof(consecutiveHours));
        }

        Start = consecutiveHours[0].Hour;
        End = consecutiveHours[^1].Hour.AddHours(1);
        HourCount = consecutiveHours.Count;
        AveragePriceTryPerKwh = consecutiveHours.Average(h => h.PriceTryPerKwh);
        MinPriceTryPerKwh = consecutiveHours.Min(h => h.PriceTryPerKwh);
    }

    public override string ToString() =>
        $"{Start:HH:mm}–{End:HH:mm} ({HourCount} saat, ort. {AveragePriceTryPerKwh:N4} TL/kWh)";
}