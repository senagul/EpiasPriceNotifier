namespace EpiasPriceNotifier.Domain.ValueObjects;

/// <summary>
/// Bir saatin elektrik takas fiyatı (PTF) snapshot'ı.
/// EPİAŞ kaynağındaki ham değer TL/MWh cinsindendir.
/// Kullanıcıya gösterim için kWh türevleri de açılır.
/// </summary>
public sealed record HourlyPrice
{
    private const decimal MwhToKwh = 1000m;

    public DateTimeOffset Hour { get; }

    public decimal PriceTryPerMwh { get; }
    public decimal PriceUsdPerMwh { get; }
    public decimal PriceEurPerMwh { get; }

    public decimal PriceTryPerKwh => PriceTryPerMwh / MwhToKwh;
    public decimal PriceUsdPerKwh => PriceUsdPerMwh / MwhToKwh;
    public decimal PriceEurPerKwh => PriceEurPerMwh / MwhToKwh;

    public HourlyPrice(
        DateTimeOffset hour,
        decimal priceTryPerMwh,
        decimal priceUsdPerMwh,
        decimal priceEurPerMwh)
    {
        if (priceTryPerMwh < 0)
            throw new ArgumentOutOfRangeException(nameof(priceTryPerMwh),
                $"Fiyat negatif olamaz: {priceTryPerMwh}");

        if (hour.Minute != 0 || hour.Second != 0)
            throw new ArgumentException(
                $"Saat tam saat olmalı (dakika/saniye 0): {hour:HH:mm:ss}",
                nameof(hour));

        Hour = hour;
        PriceTryPerMwh = priceTryPerMwh;
        PriceUsdPerMwh = priceUsdPerMwh;
        PriceEurPerMwh = priceEurPerMwh;
    }

    public bool IsFree => PriceTryPerMwh == 0m;

    /// <summary>
    /// Eşik MWh cinsindendir (EPİAŞ kaynağıyla aynı birim).
    /// kWh ile karşılaştırma için <see cref="IsCheaperThanPerKwh"/> kullanın.
    /// </summary>
    public bool IsCheaperThanPerMwh(decimal thresholdTryPerMwh) =>
        PriceTryPerMwh < thresholdTryPerMwh;

    public bool IsCheaperThanPerKwh(decimal thresholdTryPerKwh) =>
        PriceTryPerKwh < thresholdTryPerKwh;

    public override string ToString() =>
        $"{Hour:yyyy-MM-dd HH:mm} → {PriceTryPerMwh:N2} TL/MWh ({PriceTryPerKwh:N4} TL/kWh)";
}