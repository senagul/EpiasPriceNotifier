using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Application.Common.Exceptions;
using EpiasPriceNotifier.Application.UseCases.FetchAndNotifyCheapHours;
using EpiasPriceNotifier.Domain.Entities;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.Services;
using EpiasPriceNotifier.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EpiasPriceNotifier.Application.UnitTests.UseCases.FetchAndNotifyCheapHours;

/// <summary>
/// FetchAndNotifyCheapHoursHandler için unit testler.
///
/// Handler 6 bağımlılığa sahip — hepsi mock'lanıyor. Test odaklı:
/// orchestration sıralaması, idempotency davranışı, edge case'ler.
///
/// "Test for behavior, not for implementation":
/// - HasSentForDateAsync'in NE ZAMAN çağrıldığını test ediyoruz (en başta,
///   diğer servislere dokunmadan önce)
/// - RecordSentAsync'in NE ZAMAN çağrıldığını test ediyoruz (dispatch'ten sonra)
/// - Sıra çok önemli — yanlış sıra yarı-başarılı durumda sessiz veri kaybına neden olur
///
/// Test sınıfı sırasız değil; constructor'da common setup yapıyoruz,
/// her test "given X, when Y, then Z" yapısında.
/// </summary>
public class FetchAndNotifyCheapHoursHandlerTests
{
    // Tüm test'lerin paylaştığı mock'lar. Constructor'da yaratılıyor,
    // her test'in başında yeni instance (xUnit her [Fact] için sınıfı yeniden yaratır).
    private readonly IEpiasPriceClient _priceClient;
    private readonly ICheapHourAnalyzer _analyzer;
    private readonly INotificationDispatcher _dispatcher;
    private readonly IPriceThresholdProvider _thresholdProvider;
    private readonly IRecipientProvider _recipientProvider;
    private readonly INotificationLogRepository _logRepository;
    private readonly FetchAndNotifyCheapHoursHandler _sut;

    private static readonly DateOnly TestDate = new(2026, 6, 21);
    private static readonly PriceThreshold Threshold = PriceThreshold.FromTryPerKwh(0.30m);

    public FetchAndNotifyCheapHoursHandlerTests()
    {
        _priceClient = Substitute.For<IEpiasPriceClient>();
        _analyzer = Substitute.For<ICheapHourAnalyzer>();
        _dispatcher = Substitute.For<INotificationDispatcher>();
        _thresholdProvider = Substitute.For<IPriceThresholdProvider>();
        _recipientProvider = Substitute.For<IRecipientProvider>();
        _logRepository = Substitute.For<INotificationLogRepository>();

        // Common default'lar — testler bunları override edebilir
        _thresholdProvider.GetThreshold().Returns(Threshold);
        _recipientProvider.GetRecipients().Returns(new[]
        {
            new Recipient("User1", new[] { NotificationChannel.Telegram })
        });

        _sut = new FetchAndNotifyCheapHoursHandler(
            _priceClient,
            _analyzer,
            _dispatcher,
            _thresholdProvider,
            _recipientProvider,
            _logRepository,
            NullLogger<FetchAndNotifyCheapHoursHandler>.Instance);
    }

    [Fact]
    public async Task Handle_HappyPath_ExecutesFullPipelineInOrder()
    {
        // Arrange — repository "henüz gönderilmemiş" desin
        _logRepository
            .HasSentForDateAsync(TestDate, Arg.Any<CancellationToken>())
            .Returns(false);

        var schedule = BuildSchedule(0m); // tüm saatler bedava (test verisi)
        _priceClient
            .GetDailyPricesAsync(TestDate, Arg.Any<CancellationToken>())
            .Returns(schedule);

        _analyzer
            .FindCheapWindows(schedule, Threshold)
            .Returns(Array.Empty<CheapWindow>());

        // Act
        await _sut.Handle(new FetchAndNotifyCheapHoursCommand(TestDate), CancellationToken.None);

        // Assert — tüm pipeline çağrıldı
        await _logRepository.Received(1).HasSentForDateAsync(TestDate, Arg.Any<CancellationToken>());
        await _priceClient.Received(1).GetDailyPricesAsync(TestDate, Arg.Any<CancellationToken>());
        _analyzer.Received(1).FindCheapWindows(schedule, Threshold);
        _recipientProvider.Received(1).GetRecipients();
        await _dispatcher.Received(1).SendAsync(
            Arg.Any<IEnumerable<Recipient>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _logRepository.Received(1).RecordSentAsync(
            TestDate,
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAlreadySent_ReturnsEarlyWithoutFetchingOrDispatching()
    {
        // Arrange — repository "zaten gönderilmiş" desin
        _logRepository
            .HasSentForDateAsync(TestDate, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await _sut.Handle(new FetchAndNotifyCheapHoursCommand(TestDate), CancellationToken.None);

        // Assert — idempotency check'ten sonra hiçbir şey çağrılmamalı
        await _logRepository.Received(1).HasSentForDateAsync(TestDate, Arg.Any<CancellationToken>());

        // EPİAŞ'a istek atılmadı
        await _priceClient.DidNotReceive().GetDailyPricesAsync(
            Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());

        // Analyzer çağrılmadı
        _analyzer.DidNotReceive().FindCheapWindows(
            Arg.Any<DailyPriceSchedule>(), Arg.Any<PriceThreshold>());

        // Dispatcher çağrılmadı — kullanıcılar ikinci mesajı almadı
        await _dispatcher.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<Recipient>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        // Tekrar RecordSent çağrılmadı
        await _logRepository.DidNotReceive().RecordSentAsync(
            Arg.Any<DateOnly>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenNoRecipientsConfigured_SkipsDispatchAndDoesNotRecord()
    {
        // Arrange — recipient listesi boş
        _logRepository
            .HasSentForDateAsync(TestDate, Arg.Any<CancellationToken>())
            .Returns(false);

        var schedule = BuildSchedule(0m);
        _priceClient
            .GetDailyPricesAsync(TestDate, Arg.Any<CancellationToken>())
            .Returns(schedule);

        _analyzer
            .FindCheapWindows(schedule, Threshold)
            .Returns(Array.Empty<CheapWindow>());

        // Recipient provider boş liste döndürsün
        _recipientProvider.GetRecipients().Returns(Array.Empty<Recipient>());

        // Act
        await _sut.Handle(new FetchAndNotifyCheapHoursCommand(TestDate), CancellationToken.None);

        // Assert — EPİAŞ ve analyzer çağrıldı ama dispatcher ÇAĞRILMADI
        await _priceClient.Received(1).GetDailyPricesAsync(TestDate, Arg.Any<CancellationToken>());
        _analyzer.Received(1).FindCheapWindows(schedule, Threshold);

        await _dispatcher.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<Recipient>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        // RecordSent çağrılmadı — kimseye gönderim yapılmadıysa kayıt anlamsız
        await _logRepository.DidNotReceive().RecordSentAsync(
            Arg.Any<DateOnly>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenEpiasClientThrows_PropagatesExceptionAndDoesNotRecord()
    {
        // Arrange — EPİAŞ patlayacak
        _logRepository
            .HasSentForDateAsync(TestDate, Arg.Any<CancellationToken>())
            .Returns(false);

        _priceClient
            .GetDailyPricesAsync(TestDate, Arg.Any<CancellationToken>())
            .ThrowsAsync(new EpiasIntegrationException("EPİAŞ down", statusCode: 503));

        // Act
        var act = async () => await _sut.Handle(
            new FetchAndNotifyCheapHoursCommand(TestDate), CancellationToken.None);

        // Assert — exception yukarı propagate (handler yutmuyor)
        await act.Should().ThrowAsync<EpiasIntegrationException>();

        // Dispatcher çağrılmadı, log da yazılmadı
        await _dispatcher.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<Recipient>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        await _logRepository.DidNotReceive().RecordSentAsync(
            Arg.Any<DateOnly>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_RecordsAfterDispatch_NotBefore()
    {
        // Arrange — sıralama testi. Dispatch sonrasında record yapıldığını
        // doğrulamak için NSubstitute'in Received.InOrder feature'ını kullanıyoruz.
        _logRepository
            .HasSentForDateAsync(TestDate, Arg.Any<CancellationToken>())
            .Returns(false);

        var schedule = BuildSchedule(0m);
        _priceClient
            .GetDailyPricesAsync(TestDate, Arg.Any<CancellationToken>())
            .Returns(schedule);

        _analyzer
            .FindCheapWindows(schedule, Threshold)
            .Returns(Array.Empty<CheapWindow>());

        // Act
        await _sut.Handle(new FetchAndNotifyCheapHoursCommand(TestDate), CancellationToken.None);

        // Assert — Received.InOrder çağrı sırasını doğruluyor
        Received.InOrder(async () =>
        {
            await _dispatcher.SendAsync(
                Arg.Any<IEnumerable<Recipient>>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());

            await _logRepository.RecordSentAsync(
                TestDate,
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Handle_PassesCorrectRecipientCountToRecordSent()
    {
        // Arrange — 3 recipient (User1 + User2 + User3)
        _logRepository
            .HasSentForDateAsync(TestDate, Arg.Any<CancellationToken>())
            .Returns(false);

        _recipientProvider.GetRecipients().Returns(new[]
        {
            new Recipient("User1", new[] { NotificationChannel.Telegram }),
            new Recipient("User2", new[] { NotificationChannel.Email }),
            new Recipient("User3", new[] { NotificationChannel.Telegram, NotificationChannel.Email })
        });

        var schedule = BuildSchedule(0m);
        _priceClient
            .GetDailyPricesAsync(TestDate, Arg.Any<CancellationToken>())
            .Returns(schedule);

        _analyzer
            .FindCheapWindows(schedule, Threshold)
            .Returns(Array.Empty<CheapWindow>());

        // Act
        await _sut.Handle(new FetchAndNotifyCheapHoursCommand(TestDate), CancellationToken.None);

        // Assert — RecordSent recipient count = 3 ile çağrılmalı
        await _logRepository.Received(1).RecordSentAsync(
            TestDate,
            3,  // tam olarak 3 recipient
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PassesCommandDateThroughToAllDependencies()
    {
        // Arrange — command'da geçen tarih, tüm aşağı çağrılara aynen geçmeli
        var customDate = new DateOnly(2026, 7, 15);

        _logRepository
            .HasSentForDateAsync(customDate, Arg.Any<CancellationToken>())
            .Returns(false);

        var schedule = BuildScheduleForDate(customDate, 0m);
        _priceClient
            .GetDailyPricesAsync(customDate, Arg.Any<CancellationToken>())
            .Returns(schedule);

        _analyzer
            .FindCheapWindows(schedule, Threshold)
            .Returns(Array.Empty<CheapWindow>());

        // Act
        await _sut.Handle(new FetchAndNotifyCheapHoursCommand(customDate), CancellationToken.None);

        // Assert — date aynen iletilmiş
        await _logRepository.Received(1).HasSentForDateAsync(customDate, Arg.Any<CancellationToken>());
        await _priceClient.Received(1).GetDailyPricesAsync(customDate, Arg.Any<CancellationToken>());
        await _logRepository.Received(1).RecordSentAsync(
            customDate, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Test Helpers ───────────────────────────────────────────────

    private static DailyPriceSchedule BuildSchedule(decimal uniformPriceTryPerMwh) =>
        BuildScheduleForDate(TestDate, uniformPriceTryPerMwh);

    private static DailyPriceSchedule BuildScheduleForDate(
        DateOnly date, decimal uniformPriceTryPerMwh)
    {
        var hours = new List<HourlyPrice>();
        for (var h = 0; h < 24; h++)
        {
            var hour = new DateTimeOffset(
                date.Year, date.Month, date.Day,
                h, 0, 0,
                TimeSpan.FromHours(3));

            hours.Add(new HourlyPrice(
                hour: hour,
                priceTryPerMwh: uniformPriceTryPerMwh,
                priceUsdPerMwh: 0m,
                priceEurPerMwh: 0m));
        }
        return new DailyPriceSchedule(date, hours);
    }
}