using EpiasPriceNotifier.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace EpiasPriceNotifier.Infrastructure.Persistence;

/// <summary>
/// INotificationLogRepository'nin EF Core SQLite implementasyonu.
///
/// AppDbContext'i inject edip basit CRUD yapıyor — repository pattern'in
/// "thin wrapper" stilinde. Karmaşık sorgular gerekirse buraya yeni method
/// eklenir, Application'da yeni method tanımlanır (interface segregation).
///
/// Niye internal sealed?
/// Bu sınıf Infrastructure'ın iç detayı — dışarı sızmaz, sadece DI üzerinden
/// INotificationLogRepository interface'i ile erişilir.
/// </summary>
internal sealed class NotificationLogRepository : INotificationLogRepository
{
    private readonly AppDbContext _db;

    public NotificationLogRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> HasSentForDateAsync(
        DateOnly ptfDate,
        CancellationToken cancellationToken = default)
    {
        // AnyAsync EXISTS sorgusu generate eder — SELECT 1 FROM ... LIMIT 1
        // Tüm row'u çekmek yerine sadece "var mı yok mu" bilgisi.
        // .NotificationLogs DbSet'i kullanmak yerine .Set<T>() de olabilirdi;
        // DbSet daha okunaklı.
        return await _db.NotificationLogs
            .AnyAsync(x => x.PtfDate == ptfDate, cancellationToken);
    }

    public async Task RecordSentAsync(
        DateOnly ptfDate,
        int recipientCount,
        string subject,
        CancellationToken cancellationToken = default)
    {
        var log = new NotificationLog
        {
            PtfDate = ptfDate,
            SentAt = DateTime.UtcNow,
            RecipientCount = recipientCount,
            // Subject DB'de 200 karakter limitli — uzun gelirse kes.
            // Truncation domain bilgisi değil, persistence kısıtlaması.
            Subject = subject.Length > 200 ? subject[..200] : subject
        };

        _db.NotificationLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);
    }
}