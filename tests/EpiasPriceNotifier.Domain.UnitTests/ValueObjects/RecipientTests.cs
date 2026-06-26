using EpiasPriceNotifier.Domain.Enums;
using EpiasPriceNotifier.Domain.ValueObjects;
using FluentAssertions;

namespace EpiasPriceNotifier.Domain.UnitTests.ValueObjects;

/// <summary>
/// Recipient value object'inin constructor invariant'larını ve helper'larını test eder.
///
/// Saf domain test — hiç dependency yok. Bu, "Domain hep test edilebilir kalmalı"
/// prensibinin somut kanıtı.
/// </summary>
public class RecipientTests
{
    [Fact]
    public void Constructor_WithValidInputs_CreatesRecipient()
    {
        // Act
        var recipient = new Recipient(
            "User1",
            new[] { NotificationChannel.Telegram, NotificationChannel.Email });

        // Assert
        recipient.Name.Should().Be("User1");
        recipient.Channels.Should().HaveCount(2);
        recipient.Channels.Should().Contain(NotificationChannel.Telegram);
        recipient.Channels.Should().Contain(NotificationChannel.Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithEmptyName_ThrowsArgumentException(string? invalidName)
    {
        // Act
        var act = () => new Recipient(invalidName!, new[] { NotificationChannel.Telegram });

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*ad*boş*");
    }

    [Fact]
    public void Constructor_WithNullChannels_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new Recipient("User1", null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithEmptyChannels_ThrowsArgumentException()
    {
        // Act
        var act = () => new Recipient("User1", Array.Empty<NotificationChannel>());

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*en az bir kanal*");
    }

    [Fact]
    public void Constructor_WithDuplicateChannels_DeduplicatesAutomatically()
    {
        // Arrange — aynı kanalı iki kere ver
        var channels = new[]
        {
            NotificationChannel.Telegram,
            NotificationChannel.Email,
            NotificationChannel.Telegram  // duplicate!
        };

        // Act
        var recipient = new Recipient("User1", channels);

        // Assert — distinct'lendi
        recipient.Channels.Should().HaveCount(2);
        recipient.Channels.Should().Contain(NotificationChannel.Telegram);
        recipient.Channels.Should().Contain(NotificationChannel.Email);
    }

    [Theory]
    [InlineData(NotificationChannel.Telegram, true)]
    [InlineData(NotificationChannel.Email, true)]
    [InlineData(NotificationChannel.Ntfy, false)]
    public void ReceivesVia_ReturnsCorrectAnswer(
        NotificationChannel channel, bool expected)
    {
        // Arrange
        var recipient = new Recipient(
            "User1",
            new[] { NotificationChannel.Telegram, NotificationChannel.Email });

        // Act
        var result = recipient.ReceivesVia(channel);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void ToString_ContainsNameAndChannels()
    {
        // Arrange
        var recipient = new Recipient(
            "Sena",
            new[] { NotificationChannel.Telegram, NotificationChannel.Email });

        // Act
        var str = recipient.ToString();

        // Assert
        str.Should().Contain("Sena");
        str.Should().Contain("Telegram");
        str.Should().Contain("Email");
    }
}