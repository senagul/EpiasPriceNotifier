using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Infrastructure.Epias;
using EpiasPriceNotifier.Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace EpiasPriceNotifier.Infrastructure;

/// <summary>
/// Infrastructure katmanının tüm servislerini DI container'a kaydeder.
///
/// Bu pattern (her katmanda bir DI extension method) Clean Architecture
/// projelerinde standarttır — composition root (Worker/Program.cs) küçük
/// kalır, katman içsel detaylarına bulaşmaz. Yarın yeni bir Infrastructure
/// servisi eklersen Worker'a dokunmana gerek yok, sadece bu method'a ekliyorsun.
///
/// Worker/Program.cs içinden çağrımı:
///   builder.Services.AddInfrastructure(builder.Configuration);
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ──────── EPİAŞ entegrasyonu ────────────────────────────────────

        // EpiasOptions binding
        services
            .AddOptions<EpiasOptions>()
            .Bind(configuration.GetSection(EpiasOptions.SectionName))
            .ValidateOnStart();

        // Memory cache (TGT cache için lazım)
        services.AddMemoryCache();

        // CasTgtProvider — TGT yönetimi için typed HttpClient
        services.AddHttpClient<CasTgtProvider>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<EpiasOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
        });

        // EpiasPriceClient — IEpiasPriceClient arayüzüyle bind ediyoruz
        services.AddHttpClient<IEpiasPriceClient, EpiasPriceClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<EpiasOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
        });

        // ──────── Bildirim altyapısı ───────────────────────────────────

        // NotificationOptions binding
        services
            .AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName))
            .ValidateOnStart();

        // ★ Telegram Bot Client
        // Bot token configuration'dan çekilip ITelegramBotClient'a sabit
        // bağlanıyor. Singleton tutuyoruz çünkü TelegramBotClient internal
        // olarak HttpClient pool'unu yönetiyor — yeni instance her seferinde
        // socket açar, leak yapar. Resmi öneri: tek instance.
        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<NotificationOptions>>().Value;
            return new TelegramBotClient(opts.Telegram.BotToken);
        });

        // ★ Telegram sender — ITelegramBotClient'ı kullanır
        // INotificationSender olarak register etmek kritik: Dispatcher
        // IEnumerable<INotificationSender> inject ediyor, hepsini buradan
        // toplar. Discord sender eklesem, sadece bir satır daha eklerim.
        services.AddTransient<INotificationSender, TelegramNotificationSender>();

        // ★ Email sender — MailKit her gönderimde yeni SmtpClient kullanır,
        // sender'ın kendisi stateless → Transient güvenli
        services.AddTransient<INotificationSender, EmailNotificationSender>();

        // ★ Ntfy sender — typed HttpClient pattern (IHttpClientFactory altyapısı)
        // INotificationSender interface'iyle de ayrıca register ediyoruz çünkü
        // AddHttpClient default'ta concrete tipi register eder, interface'i değil.
        services.AddHttpClient<NtfyNotificationSender>();
        services.AddTransient<INotificationSender>(sp =>
            sp.GetRequiredService<NtfyNotificationSender>());

        // ★ Dispatcher — tüm sender'ları IEnumerable olarak otomatik toplar.
        // Singleton çünkü stateless ve sender lookup'u constructor'da bir kez yapılıyor.
        services.AddSingleton<INotificationDispatcher, NotificationDispatcher>();

        // Domain servisleri — stateless, Singleton güvenli
        services.AddSingleton<EpiasPriceNotifier.Domain.Services.ICheapHourAnalyzer, EpiasPriceNotifier.Domain.Services.CheapHourAnalyzer>();

        // ──────── Application'a sunulan provider'lar ────────────────────
        // Threshold ve Recipient kaynağını arayüz arkasından sağlıyoruz.
        // Handler bu arayüzleri inject ediyor, somut tipi bilmiyor.
        services.AddSingleton<EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours.IPriceThresholdProvider, Notifications.PriceThresholdProvider>();
        services.AddSingleton<EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours.IRecipientProvider, Notifications.RecipientProvider>();
        return services;
    }
}