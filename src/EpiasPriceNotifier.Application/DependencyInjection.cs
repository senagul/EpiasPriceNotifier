using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace EpiasPriceNotifier.Application;

/// <summary>
/// Application katmanının tüm servislerini DI container'a kaydeder.
///
/// Şu an sadece MediatR var; ileride FluentValidation, MediatR pipeline
/// behaviors (Logging, Validation, Retry) buraya eklenecek.
///
/// Worker/Program.cs içinden çağrımı:
///   builder.Services.AddApplication();
///
/// Niye Configuration parametresi yok?
/// Application katmanı konfigürasyona bağımlı değil — Configuration binding
/// Infrastructure'ın sorumluluğu (IPriceThresholdProvider gibi arayüzler
/// arkasında). Application katmanı saflığını koruyor.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // MediatR — bu Assembly'deki tüm IRequestHandler<T> implementasyonlarını
        // otomatik bulup register eder. FetchAndNotifyCheapHoursHandler da
        // bu tarama ile bulunuyor — manuel kayıt yok.
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());
        });

        return services;
    }
}