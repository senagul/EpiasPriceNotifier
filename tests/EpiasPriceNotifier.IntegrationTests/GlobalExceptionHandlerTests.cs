using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EpiasPriceNotifier.IntegrationTests;

/// <summary>
/// GlobalExceptionHandler için end-to-end integration test'ler.
///
/// EpiasApiFixture (WireMock) burada YETERSİZ — orası sadece HttpClient
/// seviyesinde çalışır, ASP.NET Core middleware pipeline'ı devrede değildir.
/// UseExceptionHandler middleware'ini test etmek için gerçek bir test server
/// lazım: WebApplicationFactory<Program>.
///
/// WebApplicationFactory ne yapıyor?
/// - Worker projesinin Program.cs'ini gerçek ASP.NET Core test host'unda çalıştırır
/// - HTTP üzerinden istek gönderebileceğimiz bir HttpClient verir
/// - Middleware pipeline tam çalışır — UseExceptionHandler aktiftir
/// - Process içi (in-memory) çalışır, gerçek port açmaz, hızlıdır
///
/// Mülakatta: "End-to-end test'lerimde gerçek ASP.NET Core pipeline'ı
/// kullanıyorum — middleware davranışı, exception handling, routing
/// hepsi test ediliyor. WebApplicationFactory bunun standart Microsoft yolu."
/// </summary>
public class GlobalExceptionHandlerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public GlobalExceptionHandlerTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task NotFoundException_ReturnsProblemDetailsWith404()
    {
        // Act
        var response = await _client.GetAsync("/test/error/notfound");

        // Assert — status code
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Assert — content type RFC 7807 standardı
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json");

        // Assert — ProblemDetails içeriği
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(404);
        problem.Title.Should().Be("Kayıt bulunamadı");
        problem.Type.Should().Be("NotFoundException");
        problem.Instance.Should().Be("/test/error/notfound");
        problem.Detail.Should().Contain("DailyPriceSchedule");
    }

    [Fact]
    public async Task ValidationException_ReturnsProblemDetailsWith400AndErrors()
    {
        // Act
        var response = await _client.GetAsync("/test/error/validation");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(400);
        problem.Title.Should().Be("Geçersiz istek");
        problem.Type.Should().Be("ValidationException");

        // ProblemDetails.Extensions'da "errors" key'i olmalı (Microsoft convention)
        // Field bazlı hatalar nested dictionary olarak gelir.
        problem.Extensions.Should().ContainKey("errors");
    }

    [Fact]
    public async Task EpiasIntegrationException_ReturnsProblemDetailsWith502()
    {
        // Act
        var response = await _client.GetAsync("/test/error/epias");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(502);
        problem.Type.Should().Be("EpiasIntegrationException");
        problem.Title.Should().Contain("EPİAŞ");

        // Upstream status code Extensions'a koyuldu mu
        problem.Extensions.Should().ContainKey("epiasStatusCode");
    }

    [Fact]
    public async Task UnknownException_ReturnsProblemDetailsWith500()
    {
        // Switch expression'ın default arm'ını (_) test ediyor.
        // Bilinmeyen exception tipleri 500 olmalı, leak olmamalı.

        // Act
        var response = await _client.GetAsync("/test/error/unhandled");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(500);
        problem.Title.Should().Be("Beklenmedik bir hata oluştu");
        problem.Type.Should().Be("InvalidOperationException");
    }
}