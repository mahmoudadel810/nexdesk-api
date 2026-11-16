using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NexDesk.Api.Endpoints;

namespace NexDesk.IntegrationTests.Infrastructure;

/// <summary>A workspace with a verified, signed-in owner, created through the public API.</summary>
public sealed record TestWorkspace(
    HttpClient Client,
    Guid TenantId,
    Guid OwnerId,
    string Slug,
    string Email,
    string Password,
    string AccessToken,
    string RefreshCookie);

public static class TestWorkspaces
{
    public const string DefaultPassword = "Passw0rd-123";

    public static string UniqueEmail(string prefix = "owner") => $"{prefix}-{Guid.NewGuid():N}@example.test";

    /// <summary>A client that neither stores cookies nor follows redirects, so tests control both.</summary>
    public static HttpClient CreateBareClient(this ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

    public static RegisterRequest NewRegistration(string? email = null, string? workspaceName = null, string? slug = null) =>
        new(
            workspaceName ?? $"Acme {Guid.NewGuid():N}"[..20],
            slug,
            "Olivia Owner",
            email ?? UniqueEmail(),
            DefaultPassword,
            Language: null,
            Timezone: null);

    public static async Task<TestWorkspace> CreateWorkspaceAsync(this ApiFactory factory, string? workspaceName = null)
    {
        var client = factory.CreateBareClient();
        var registration = NewRegistration(workspaceName: workspaceName);

        var registered = await client.PostAsJsonAsync("/api/v1/auth/register", registration);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await registered.Content.ReadFromJsonAsync<JsonElement>();

        var token = factory.Emails.Last(registration.Email, "verify-email").Token;
        var verified = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(token));
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        var auth = (await verified.Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new TestWorkspace(
            client,
            created.GetProperty("tenantId").GetGuid(),
            auth.Session.User.Id,
            created.GetProperty("slug").GetString()!,
            registration.Email,
            registration.Password,
            auth.AccessToken,
            verified.ReadRefreshCookie()!);
    }

    public static string? ReadRefreshCookie(this HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        var prefix = AuthEndpoints.RefreshCookie + "=";
        var cookie = cookies.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return cookie?[prefix.Length..].Split(';')[0];
    }

    public static HttpRequestMessage WithRefreshCookie(this HttpRequestMessage request, string value)
    {
        request.Headers.Add("Cookie", $"{AuthEndpoints.RefreshCookie}={value}");
        return request;
    }

    public static Task<HttpResponseMessage> RefreshAsync(this HttpClient client, string refreshCookie) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh").WithRefreshCookie(refreshCookie));

    public static async Task<string> ProblemCodeAsync(this HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("code").GetString()!;
    }
}
