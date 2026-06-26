using Microsoft.EntityFrameworkCore;

namespace EpiasPriceNotifier.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext. Şu an sadece NotificationLog tablosu var.
///
/// SQLite kullanıyoruz: dosya tabanlı, kurulum gerekmiyor, Bu use case için (günde 1
/// bildirim, concurrent yazma yok) idealdir.
///
/// İleride production scale gerekirse PostgreSQL'e geçiş kolay — EF abstraction
/// sayesinde DbContext aynı kalır, sadece connection string ve provider değişir.
///
/// Niye AppDbContext, NotificationDbContext değil?
/// Tek bir DbContext yeterli; bütün uygulamanın veri katmanı burada. Her
/// modülün ayrı DbContext'i (DDD bounded context yaklaşımı) projeye karmaşıklık
/// katar — basit bir bildirim aracı için over-engineering.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // NotificationLog tablo konfigürasyonu
        modelBuilder.Entity<NotificationLog>(entity =>
        {
            // PtfDate üzerine UNIQUE INDEX — idempotency garantisi DB seviyesinde.
            // Application "aynı gün için var mı" check yapsa bile, race condition'da
            // iki istek aynı anda eklemeye çalışırsa DB UNIQUE constraint patlatır.
            // "Defense in depth" — application + database iki katman koruma.
            entity.HasIndex(x => x.PtfDate).IsUnique();

            // Subject sınırı — uzun mesajlar storage şişirmesin, log için zaten
            // kısa subject yazıyoruz (~50-80 karakter)
            entity.Property(x => x.Subject)
                  .HasMaxLength(200)
                  .IsRequired();
        });
    }
}