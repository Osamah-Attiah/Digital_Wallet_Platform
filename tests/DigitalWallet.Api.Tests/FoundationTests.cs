using System.Text.Json;
using DigitalWallet.Api.Foundation.Configuration;
using DigitalWallet.Api.Foundation.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DigitalWallet.Api.Tests;

public sealed class FoundationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_endpoint_returns_and_echoes_a_valid_correlation_id()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(ProblemDetailsFactory.CorrelationIdHeaderName, "request_123-abc");

        var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("request_123-abc", response.Headers.GetValues(ProblemDetailsFactory.CorrelationIdHeaderName).Single());
    }

    [Fact]
    public async Task Unknown_route_returns_a_problem_details_contract_with_correlation_id()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/not-found");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("HTTP_ERROR", json.RootElement.GetProperty("code").GetString());
        Assert.True(json.RootElement.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task Unhandled_exception_is_safely_mapped_to_problem_details()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Items[ProblemDetailsFactory.CorrelationIdItemName] = "test-correlation";
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("sensitive detail"), CancellationToken.None);

        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("INTERNAL_ERROR", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("test-correlation", json.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("sensitive detail", json.RootElement.ToString());
    }

    [Fact]
    public void Invalid_platform_configuration_is_rejected()
    {
        var options = new PlatformOptions
        {
            BusinessTimeZone = "not-a-time-zone",
            PostgreSql = new PostgreSqlOptions { ConnectionString = "Database=wallet" }
        };

        var result = new PlatformOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("BusinessTimeZone", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("Host and Database", StringComparison.Ordinal));
    }
}
