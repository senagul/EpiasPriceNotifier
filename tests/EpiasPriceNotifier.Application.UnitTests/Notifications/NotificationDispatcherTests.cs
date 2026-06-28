using EpiasPriceNotifier.Application.Abstractions;
using EpiasPriceNotifier.Application.Common.Exceptions;
using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;
using EpiasPriceNotifier.Infrastructure.Notifications;
using EpiasPriceNotifier.Infrastructure.Observability;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EpiasPriceNotifier.Application.UnitTests.Notifications;

public class NotificationDispatcherTests
{
    [Fact]
    public async Task SendAsync_RoutesMessageToCorrectSenders()
    {
        var telegramSender = Substitute.For<INotificationSender>();
        telegramSender.Channel.Returns(NotificationChannel.Telegram);

        var emailSender = Substitute.For<INotificationSender>();
        emailSender.Channel.Returns(NotificationChannel.Email);

        var dispatcher = CreateDispatcher(telegramSender, emailSender);

        var recipient = new Recipient("User1", new[] { NotificationChannel.Telegram });

        var result = await dispatcher.SendAsync(new[] { recipient }, "test subject", "test body");

        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(0);
        result.HasAnySuccess.Should().BeTrue();

        await telegramSender.Received(1).SendAsync(Arg.Is<Recipient>(r => r.Name == "User1"), "test subject", "test body", Arg.Any<CancellationToken>());
        await emailSender.DidNotReceive().SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithMultipleRecipientsAndChannels_DispatchesAllCombinations()
    {
        var telegramSender = Substitute.For<INotificationSender>();
        telegramSender.Channel.Returns(NotificationChannel.Telegram);

        var emailSender = Substitute.For<INotificationSender>();
        emailSender.Channel.Returns(NotificationChannel.Email);

        var dispatcher = CreateDispatcher(telegramSender, emailSender);

        var user1 = new Recipient("User1", new[] { NotificationChannel.Telegram, NotificationChannel.Email });
        var user2 = new Recipient("User2", new[] { NotificationChannel.Email });

        var result = await dispatcher.SendAsync(new[] { user1, user2 }, "s", "b");

        result.SuccessCount.Should().Be(3); // User1 Telegram + User1 Email + User2 Email
        result.FailureCount.Should().Be(0);

        await telegramSender.Received(1).SendAsync(Arg.Is<Recipient>(r => r.Name == "User1"), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await emailSender.Received(2).SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await emailSender.Received(1).SendAsync(Arg.Is<Recipient>(r => r.Name == "User1"), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await emailSender.Received(1).SendAsync(Arg.Is<Recipient>(r => r.Name == "User2"), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WhenOneSenderThrows_OtherSendersStillComplete()
    {
        var telegramSender = Substitute.For<INotificationSender>();
        telegramSender.Channel.Returns(NotificationChannel.Telegram);
        telegramSender.SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new NotificationSendException(NotificationChannel.Telegram, "User1", "Telegram down"));

        var emailSender = Substitute.For<INotificationSender>();
        emailSender.Channel.Returns(NotificationChannel.Email);

        var dispatcher = CreateDispatcher(telegramSender, emailSender);

        var recipient = new Recipient("User1", new[] { NotificationChannel.Telegram, NotificationChannel.Email });

        var act = async () => await dispatcher.SendAsync(new[] { recipient }, "s", "b");

        var result = await act.Should().NotThrowAsync();
        result.Subject.SuccessCount.Should().Be(1); // Email baþarýlý
        result.Subject.FailureCount.Should().Be(1); // Telegram patladý
        result.Subject.HasAnySuccess.Should().BeTrue();

        await telegramSender.Received(1).SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await emailSender.Received(1).SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WhenAllSendersThrow_ReturnsAllFailures()
    {
        // Yeni test: tüm kanallar patladýysa SuccessCount = 0, HasAnySuccess = false.
        // Bu davranýþ handler'ýn "kayýt atma" mantýðýnýn temeli.
        var telegramSender = Substitute.For<INotificationSender>();
        telegramSender.Channel.Returns(NotificationChannel.Telegram);
        telegramSender.SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new NotificationSendException(NotificationChannel.Telegram, "User1", "Telegram down"));

        var emailSender = Substitute.For<INotificationSender>();
        emailSender.Channel.Returns(NotificationChannel.Email);
        emailSender.SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new NotificationSendException(NotificationChannel.Email, "User1", "Email down"));

        var dispatcher = CreateDispatcher(telegramSender, emailSender);

        var recipient = new Recipient("User1", new[] { NotificationChannel.Telegram, NotificationChannel.Email });

        var result = await dispatcher.SendAsync(new[] { recipient }, "s", "b");

        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(2);
        result.HasAnySuccess.Should().BeFalse();
        result.AllSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_WhenRecipientWantsUnregisteredChannel_GracefullySkips()
    {
        var telegramSender = Substitute.For<INotificationSender>();
        telegramSender.Channel.Returns(NotificationChannel.Telegram);

        var dispatcher = CreateDispatcher(telegramSender);

        var recipient = new Recipient("User1", new[] { NotificationChannel.Telegram, NotificationChannel.Email });

        var result = await dispatcher.SendAsync(new[] { recipient }, "s", "b");

        result.SuccessCount.Should().Be(1); // Telegram baþarýlý
        result.FailureCount.Should().Be(0); // Email kanal yok, skip, hata sayýlmýyor

        await telegramSender.Received(1).SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithEmptyRecipientList_ReturnsEmptyResult()
    {
        var sender = Substitute.For<INotificationSender>();
        sender.Channel.Returns(NotificationChannel.Telegram);

        var dispatcher = CreateDispatcher(sender);

        var result = await dispatcher.SendAsync(Array.Empty<Recipient>(), "s", "b");

        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
        result.NoAttempts.Should().BeTrue();
        result.HasAnySuccess.Should().BeFalse();

        await sender.DidNotReceive().SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WithNullRecipients_ThrowsArgumentNullException()
    {
        var sender = Substitute.For<INotificationSender>();
        sender.Channel.Returns(NotificationChannel.Telegram);

        var dispatcher = CreateDispatcher(sender);

        var act = async () => await dispatcher.SendAsync(null!, "s", "b");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SendAsync_WhenSenderThrowsUnexpectedException_CountsAsFailure()
    {
        var sender = Substitute.For<INotificationSender>();
        sender.Channel.Returns(NotificationChannel.Telegram);
        sender.SendAsync(Arg.Any<Recipient>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("unexpected"));

        var dispatcher = CreateDispatcher(sender);

        var recipient = new Recipient("User1", new[] { NotificationChannel.Telegram });

        var result = await dispatcher.SendAsync(new[] { recipient }, "s", "b");

        // Beklenmedik exception da yutuldu, failure sayýldý, exception fýrlatýlmadý
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(1);
        result.HasAnySuccess.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithTwoSendersForSameChannel_Throws()
    {
        var sender1 = Substitute.For<INotificationSender>();
        sender1.Channel.Returns(NotificationChannel.Telegram);

        var sender2 = Substitute.For<INotificationSender>();
        sender2.Channel.Returns(NotificationChannel.Telegram);

        var act = () => CreateDispatcher(sender1, sender2);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Test helper — NotificationDispatcher'ý gerçek bir EpiasMetrics instance'ý
    /// ile yaratýr. Metric'lerin observable tarafýný test etmiyoruz (kapsam dýþý),
    /// sadece dispatcher davranýþý için gerçek bir non-null Meter yeterli.
    /// </summary>
    private static NotificationDispatcher CreateDispatcher(params INotificationSender[] senders) => new(senders, NullLogger<NotificationDispatcher>.Instance, new EpiasMetrics());
}