namespace EpiasPriceNotifier.Domain.ValueObjects;

/// <summary>
/// Fiyat eşiği — "bu değerin altındaki saatler ucuz sayılır" kuralını taşır.
/// Birim TL/kWh (kullanıcı dostu birim).
/// </summary>
public readonly record struct PriceThreshold
{
    public decimal AmountTryPerKwh { get; }

    public PriceThreshold(decimal amountTryPerKwh)
    {
        if (amountTryPerKwh < 0)
            throw new ArgumentOutOfRangeException(nameof(amountTryPerKwh),
                $"Eşik negatif olamaz: {amountTryPerKwh}");

        AmountTryPerKwh = amountTryPerKwh;
    }

    public static PriceThreshold Free => new(0m);

    public static PriceThreshold FromTryPerKwh(decimal amount) => new(amount);

    public static PriceThreshold FromTryPerMwh(decimal amount) => new(amount / 1000m);

    public bool Includes(HourlyPrice price) => price.PriceTryPerKwh < AmountTryPerKwh;

    public override string ToString() => $"< {AmountTryPerKwh:N4} TL/kWh";
}