using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace EpiasPriceNotifier.IntegrationTests;

/// <summary>
/// EpiasPriceClient için integration test'ler.
/// Gerçek HttpClient → WireMock → JSON → Domain mapper akışını test eder.
/// EPİAŞ'a hiç bağlanmıyoruz; tüm akış lokal, deterministik, hızlı.
/// </summary>
public class EpiasPriceClientTests : IClassFixture<EpiasApiFixture>
{
    private readonly EpiasApiFixture _fixture;

    public EpiasPriceClientTests(EpiasApiFixture fixture)
    {
        _fixture = fixture;
        // Her test'ten önce stub'ları temizle — testler birbirini etkilemesin.
        _fixture.ResetStubs();
    }

    [Fact]
    public async Task GetDailyPricesAsync_HappyPath_ReturnsSchedule()
    {
        // Arrange — sahte EPİAŞ kur:
        // 1) CAS endpoint TGT döndürsün
        // 2) MCP endpoint 24 saatlik fiyat döndürsün
        var testDate = new DateOnly(2026, 6, 18);

        _fixture.Server
            .Given(Request.Create()
                .WithPath("/cas/v1/tickets")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithHeader("Content-Type", "text/plain")
                .WithBody(EpiasTestData.FakeTgt));

        _fixture.Server
            .Given(Request.Create()
                .WithPath("/v1/markets/dam/data/mcp")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(EpiasTestData.McpResponseJsonUniform(testDate, pricePerMwh: 250m)));

        // Act
        var schedule = await _fixture.Client.GetDailyPricesAsync(testDate);

        // Assert
        schedule.Should().NotBeNull();
        schedule.Date.Should().Be(testDate);
        schedule.Hours.Should().HaveCount(24);
        schedule.Hours.Should().AllSatisfy(h => h.PriceTryPerMwh.Should().Be(250m));
        schedule.AverageTryPerMwh.Should().Be(250m);
    }

    [Fact]
    public async Task GetDailyPricesAsync_SendsTgtHeader_OnMcpRequest()
    {
        // Arrange
        var testDate = new DateOnly(2026, 6, 18);

        _fixture.Server
            .Given(Request.Create().WithPath("/cas/v1/tickets").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithBody(EpiasTestData.FakeTgt));

        _fixture.Server
            .Given(Request.Create()
                .WithPath("/v1/markets/dam/data/mcp")
                .UsingPost()
                .WithHeader("TGT", EpiasTestData.FakeTgt)) // ← header eşleşmesi şart
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(EpiasTestData.McpResponseJsonUniform(testDate, 100m)));

        // Act
        var schedule = await _fixture.Client.GetDailyPricesAsync(testDate);

        // Assert — WireMock TGT header'ı olmayan istek için stub bulamayıp 404 dönerdi
        // → exception fırlardı. Buraya gelmek = TGT doğru header'da gönderildi.
        schedule.Should().NotBeNull();
    }

    [Fact]
    public async Task GetDailyPricesAsync_WhenMcpReturns401_InvalidatesTgtAndRetries()
    {
        // Arrange — CAS iki kez çağrılabilir (cache invalidate edildiği için).
        // MCP ise ilk denemede 401, ikinci denemede 200 dönecek (WireMock scenario state).
        var testDate = new DateOnly(2026, 6, 18);

        _fixture.Server
            .Given(Request.Create().WithPath("/cas/v1/tickets").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBody(EpiasTestData.FakeTgt));

        // İlk MCP isteği 401 dönsün, state'i "after-first-call" yapsın
        _fixture.Server
            .Given(Request.Create().WithPath("/v1/markets/dam/data/mcp").UsingPost())
            .InScenario("retry")
            .WillSetStateTo("after-first-call")
            .RespondWith(Response.Create().WithStatusCode(401));

        // İkinci MCP isteği (state "after-first-call" iken) başarılı dönsün
        _fixture.Server
            .Given(Request.Create().WithPath("/v1/markets/dam/data/mcp").UsingPost())
            .InScenario("retry")
            .WhenStateIs("after-first-call")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(EpiasTestData.McpResponseJsonUniform(testDate, 200m)));

        // Act
        var schedule = await _fixture.Client.GetDailyPricesAsync(testDate);

        // Assert — retry başarılı oldu, schedule döndü
        schedule.Should().NotBeNull();
        schedule.Hours.Should().HaveCount(24);

        // MCP endpoint'i 2 kez çağrıldığını doğrula (ilk 401, sonra 200)
        var mcpCalls = _fixture.Server.LogEntries
            .Where(e => e.RequestMessage.Path == "/v1/markets/dam/data/mcp")
            .ToList();
        mcpCalls.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetDailyPricesAsync_WhenMcpReturnsLessThan24Hours_ThrowsViaDomainInvariant()
    {
        // Arrange — sadece 12 saat içeren bozuk JSON
        var testDate = new DateOnly(2026, 6, 18);
        var only12Hours = new decimal[12];
        Array.Fill(only12Hours, 100m);

        _fixture.Server
            .Given(Request.Create().WithPath("/cas/v1/tickets").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBody(EpiasTestData.FakeTgt));

        _fixture.Server
            .Given(Request.Create().WithPath("/v1/markets/dam/data/mcp").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(EpiasTestData.McpResponseJson(testDate, only12Hours)));

        // Act
        var act = async () => await _fixture.Client.GetDailyPricesAsync(testDate);

        // Assert — Domain'in DailyPriceSchedule constructor invariant'ı yakalıyor.
        // Bu integration test'in en güzel kanıtı: malformed data Infrastructure'da değil,
        // Domain'de yakalanıyor. Domain her zaman koruyucu.
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*24*saat*");
    }
}