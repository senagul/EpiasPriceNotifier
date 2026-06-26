using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Application.Common.Exceptions;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;
using EpiasPriceNotifier.Infrastructure.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EpiasPriceNotifier.Application.UnitTests.Notifications;

/// <summary>
/// NotificationDispatcher davranışlarını mocked sender'larla doğrular.
///
/// Mimari not: Dispatcher Infrastructure katmanında yaşar ama davranışı
/// Application sözleşmesi (INotificationSender) üzerinden test ediyoruz.
/// "Outside-in testing" — implementation detail değil, observable behavior.
/// Bu yüzden test sınıfı Application.UnitTests'te yer alır.
///
/// Bu test'ler dispatcher'ın 4 kritik özelliğini koruyor:
///   1) Routing — her recipient SADECE seçtiği kanallardan mesaj alır
///   2) Fault isolation — bir kanal patlasa diğer kanal/recipient'lar etkilenmez
///   3) Graceful skip — recipient bilinmeyen kanal isterse (DI'da kayıtlı değil) skip
///   4) Empty input — recipient listesi boşsa sessizce çık, exception yok
/// </summary>
public class NotificationDispatcherTests
{
    [Fact]
    public async Task SendAsync_RoutesMessageToCorrectSenders()
    {
        // Arrange — 2 farklı sender (Telegram + Email), bir recipient sadece Telegram
        var telegramSender = Substitute.For<INotificationSender>();
        telegramSender.Channel.Returns(NotificationChannel.Telegram);

        var emailSender = Substitute.For<INotificationSender>();
        emailSender.Channel.Returns(NotificationChannel.Email);

        var dispatcher = new NotificationDispatcher(
            new[] { telegramSender, emailSender },
            NullLogger<NotificationDispatcher>.Instance);

        var recipient = new Recipient(
            "User1",
            new[] { NotificationChannel.Telegram }); // SADECE Telegram

        // Act
        await dispatcher.SendAsync(
            new[] { recipient },
            "test subject",
            "test body");

        // Assert
        // Telegram sender çağrılmış olmalı (recipient + subject + body ile)
        await telegramSender.Received(1).SendAsync(
            Arg.Is<Recipient>(r => r.Name == "User1"),
            "test subject",
            "test body",
            Arg.Any<CancellationToken>());

        // Email sender'ın hiç çağrılmaması lazım — recipient Email istemedi
        await emailSender.DidNotReceive().SendAsync(
            Arg.Any<Recipient>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithMultipleRecipientsAndChannels_DispatchesAllCombinations()
    {
        // Arrange — 2 sender, 2 recipient (farklı kanal seçimleriyle)
        var telegramSender = Substitute.For<INotificationSender>();
        telegramSender.Channel.Returns(NotificationChannel.Telegram);

        var emailSender = Substitute.For<INotificationSender>();
        emailSender.Channel.Returns(NotificationChannel.Email);

        var dispatcher = new NotificationDispatcher(
            new[] { telegramSender, emailSender },
            NullLogger<NotificationDispatcher>.Instance);

        var user1 = new Recipient(
            "User1",
            new[] { NotificationChannel.Telegram, NotificationChannel.Email });
        var user2 = new Recipient(
            "User2",
            new[] { NotificationChannel.Email });

        // Act
        await dispatcher.SendAsync(new[] { user1, user2 }, "s", "b");

        // Assert — Telegram sender SADECE User1 için çağrıldı (1 kez)
        await telegramSender.Received(1).SendAsync(
            Arg.Is<Recipient>(r => r.Name == "User1"),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Email sender hem User1 hem User2 için çağrıldı (2 kez toplam)
        await emailSender.Received(2).SendAsync(
            Arg.Any<Recipient>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Spesifikleştir: Email sender hem User1 hem User2'yi gördü mü
        await emailSender.Received(1).SendAsync(
            Arg.Is<Recipient>(r => r.Name == "User1"),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await emailSender.Received(1).SendAsync(
            Arg.Is<Recipient>(r => r.Name == "User2"),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WhenOneSenderThrows_OtherSendersStillComplete()
    {
        // Arrange — Telegram patlayacak, Email çalışacak
        var telegramSender = Substitute.For<INotificationSender>();
        telegramSender.Channel.Returns(NotificationChannel.Telegram);
        telegramSender
            .SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotificationSendException(
                NotificationChannel.Telegram, "User1", "Telegram down"));

        var emailSender = Substitute.For<INotificationSender>();
        emailSender.Channel.Returns(NotificationChannel.Email);

        var dispatcher = new NotificationDispatcher(
            new[] { telegramSender, emailSender },
            NullLogger<NotificationDispatcher>.Instance);

        var recipient = new Recipient(
            "User1",
            new[] { NotificationChannel.Telegram, NotificationChannel.Email });

        // Act — dispatcher EXCEPTION FIRLATMAMALI, içeride yutmalı
        var act = async () =>
            await dispatcher.SendAsync(new[] { recipient }, "s", "b");

        // Assert — fırlatmadı (en kritik garanti)
        await act.Should().NotThrowAsync();

        // Telegram denenmiş (ve patlamış)
        await telegramSender.Received(1).SendAsync(
            Arg.Any<Recipient>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Email yine de çağrıldı — fault isolation kanıtlandı
        await emailSender.Received(1).SendAsync(
            Arg.Any<Recipient>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WhenRecipientWantsUnregisteredChannel_GracefullySkips()
    {
        // Arrange — sadece Telegram sender kayıtlı, ama recipient Email istiyor
        var telegramSender = Substitute.For<INotificationSender>();
        telegramSender.Channel.Returns(NotificationChannel.Telegram);

        var dispatcher = new NotificationDispatcher(
            new[] { telegramSender },
            NullLogger<NotificationDispatcher>.Instance);

        var recipient = new Recipient(
            "User1",
            new[] { NotificationChannel.Telegram, NotificationChannel.Email }); // Email kayıtlı değil

        // Act
        var act = async () =>
            await dispatcher.SendAsync(new[] { recipient }, "s", "b");

        // Assert — patlamamalı, Telegram'ı çağırmalı, Email'i atlamalı
        await act.Should().NotThrowAsync();

        await telegramSender.Received(1).SendAsync(
            Arg.Any<Recipient>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithEmptyRecipientList_DoesNothingAndReturns()
    {
        // Arrange
        var sender = Substitute.For<INotificationSender>();
        sender.Channel.Returns(NotificationChannel.Telegram);

        var dispatcher = new NotificationDispatcher(
            new[] { sender },
            NullLogger<NotificationDispatcher>.Instance);

        // Act
        var act = async () =>
            await dispatcher.SendAsync(Array.Empty<Recipient>(), "s", "b");

        // Assert — exception yok, sender da hiç çağrılmadı
        await act.Should().NotThrowAsync();

        await sender.DidNotReceive().SendAsync(
            Arg.Any<Recipient>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithNullRecipients_ThrowsArgumentNullException()
    {
        // Arrange
        var sender = Substitute.For<INotificationSender>();
        sender.Channel.Returns(NotificationChannel.Telegram);

        var dispatcher = new NotificationDispatcher(
            new[] { sender },
            NullLogger<NotificationDispatcher>.Instance);

        // Act
        var act = async () => await dispatcher.SendAsync(null!, "s", "b");

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SendAsync_WhenSenderThrowsUnexpectedException_StillCompletes()
    {
        // Arrange — sender bilinmeyen tip exception fırlatır (örn. NRE)
        // Dispatcher bunu da yutmalı; sadece NotificationSendException değil
        // her exception'ı izole etmeli (OperationCanceledException hariç).
        var sender = Substitute.For<INotificationSender>();
        sender.Channel.Returns(NotificationChannel.Telegram);
        sender
            .SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("unexpected"));

        var dispatcher = new NotificationDispatcher(
            new[] { sender },
            NullLogger<NotificationDispatcher>.Instance);

        var recipient = new Recipient("User1", new[] { NotificationChannel.Telegram });

        // Act
        var act = async () =>
            await dispatcher.SendAsync(new[] { recipient }, "s", "b");

        // Assert — fırlatmamalı (SafeSendAsync wrapper exception yutuyor)
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Constructor_WithTwoSendersForSameChannel_Throws()
    {
        // Arrange — iki sender aynı Channel'ı ilan ediyor (config hatası)
        var sender1 = Substitute.For<INotificationSender>();
        sender1.Channel.Returns(NotificationChannel.Telegram);

        var sender2 = Substitute.For<INotificationSender>();
        sender2.Channel.Returns(NotificationChannel.Telegram);

        // Act — dispatcher constructor'da ToDictionary fail eder
        var act = () => new NotificationDispatcher(
            new[] { sender1, sender2 },
            NullLogger<NotificationDispatcher>.Instance);

        // Assert — startup'ta erken yakala, runtime'da değil. Fail-fast.
        act.Should().Throw<ArgumentException>();
    }
}