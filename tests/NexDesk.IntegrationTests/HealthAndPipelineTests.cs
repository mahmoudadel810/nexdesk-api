using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class HealthAndPipelineTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Liveness_returns_healthy_without_touching_dependencies()
    {
        var response = await _client.GetAsync("/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().ShouldBe("Healthy");
        body.GetProperty("checks").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Readiness_reports_the_database_check()
    {
        var response = await _client.GetAsync("/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var check = body.GetProperty("checks").EnumerateArray().Single();
        check.GetProperty("name").GetString().ShouldBe("postgres");
        check.GetProperty("status").GetString().ShouldBe("Healthy");
    }

    [Fact]
    public async Task Responses_echo_a_supplied_correlation_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/meta");
        request.Headers.Add("X-Correlation-Id", "test-correlation-123");

        var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Correlation-Id").Single().ShouldBe("test-correlation-123");
    }

    [Fact]
    public async Task Responses_get_a_generated_correlation_id_when_none_or_an_unsafe_one_is_supplied()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/meta");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "bad value with spaces");

        var response = await _client.SendAsync(request);

        var correlationId = response.Headers.GetValues("X-Correlation-Id").Single();
        correlationId.ShouldNotBe("bad value with spaces");
        Guid.TryParseExact(correlationId, "N", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_routes_return_problem_details()
    {
        var response = await _client.GetAsync("/api/v1/does-not-exist");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().ShouldBe(404);
        body.TryGetProperty("correlationId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("paths").TryGetProperty("/api/v1/meta", out _).ShouldBeTrue();
    }
}
