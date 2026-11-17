using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>A staff member who joined a workspace through an invitation.</summary>
public sealed record TestMember(HttpClient Client, Guid UserId, string Email, string Password, string RefreshCookie);

public static class TestMembers
{
    public static async Task<TestMember> AddMemberAsync(
        this ApiFactory factory,
        TestWorkspace workspace,
        NexDesk.Domain.Identity.Role role,
        string fullName = "Aaron Agent")
    {
        var email = TestWorkspaces.UniqueEmail(role.ToString().ToLowerInvariant());
        var invited = await workspace.Client.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(email, role));
        invited.StatusCode.ShouldBe(HttpStatusCode.Created);

        var client = factory.CreateBareClient();
        var token = factory.Emails.Last(email, "invitation").Token;
        var accepted = await client.PostAsJsonAsync(
            "/api/v1/invitations/accept",
            new AcceptInvitationRequest(token, fullName, TestWorkspaces.DefaultPassword));
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var auth = (await accepted.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new TestMember(client, auth.Session.User.Id, email, TestWorkspaces.DefaultPassword, accepted.ReadRefreshCookie()!);
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Options))!;
    }

    public static Task<T> GetAsync<T>(this HttpClient client, string url) =>
        client.GetFromJsonAsync<T>(url, TestJson.Options)!;
}

public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

public static class TestTickets
{
    public static CreateTicketRequest NewTicket(
        string subject = "Cannot sign in",
        string? requesterEmail = null,
        NexDesk.Domain.Tickets.TicketPriority priority = NexDesk.Domain.Tickets.TicketPriority.Normal,
        Guid? assigneeId = null,
        Guid? teamId = null,
        IReadOnlyCollection<string>? tags = null,
        IReadOnlyCollection<Guid>? attachmentIds = null) => new(
            subject,
            "I get an error every time I try to sign in.",
            RequesterId: null,
            RequesterEmail: requesterEmail ?? TestWorkspaces.UniqueEmail("customer"),
            RequesterName: "Casey Customer",
            priority,
            NexDesk.Domain.Tickets.TicketType.Incident,
            assigneeId,
            teamId,
            tags,
            attachmentIds);

    public static async Task<NexDesk.Application.Tickets.TicketDetailDto> CreateTicketAsync(
        this HttpClient client,
        CreateTicketRequest? request = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/tickets", request ?? NewTicket(), TestJson.Options);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<NexDesk.Application.Tickets.TicketDetailDto>(TestJson.Options))!;
    }

    public static async Task<NexDesk.Application.Tickets.AttachmentDto> UploadAsync(
        this HttpClient client,
        string fileName,
        byte[] content,
        string contentType = "text/plain")
    {
        var response = await client.PostAsync("/api/v1/attachments", FileForm(fileName, content, contentType));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<NexDesk.Application.Tickets.AttachmentDto>(TestJson.Options))!;
    }

    public static MultipartFormDataContent FileForm(string fileName, byte[] content, string contentType = "text/plain")
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }
}

public static class TestSla
{
    /// <summary>Runs the SLA sweep for one tenant as if the clock showed <paramref name="now"/>.</summary>
    public static async Task<int> SweepSlaAsync(this ApiFactory factory, Guid tenantId, DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<NexDesk.Application.Common.ITenantScopeFactory>().Begin(tenantId);
        return await scope.ServiceProvider.GetRequiredService<NexDesk.Application.Sla.SlaEngine>()
            .SweepAsync(now, CancellationToken.None);
    }

    /// <summary>Opens the workspace around the clock, so deadlines are plain clock time and tests stay deterministic.</summary>
    public static async Task OpenAroundTheClockAsync(this HttpClient client)
    {
        var days = Enum.GetValues<DayOfWeek>()
            .Select(day => new NexDesk.Application.Sla.BusinessDayDto(day, true, new TimeOnly(0, 0), new TimeOnly(23, 59, 59)))
            .ToList();
        var response = await client.PutAsJsonAsync(
            "/api/v1/sla/business-hours",
            new UpdateBusinessHoursRequest("UTC", days),
            TestJson.Options);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

public static class TestAutomation
{
    /// <summary>Runs the time-based rules of one workspace as if the clock showed <paramref name="now"/>.</summary>
    public static async Task<int> SweepAutomationAsync(this ApiFactory factory, Guid tenantId, DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<NexDesk.Application.Common.ITenantScopeFactory>().Begin(tenantId);
        return await scope.ServiceProvider.GetRequiredService<NexDesk.Application.Automation.AutomationEngine>()
            .SweepAsync(now, CancellationToken.None);
    }
}

public static class TestBilling
{
    public const string ValidCard = "4242 4242 4242 4242";

    public static CheckoutRequest Card(string planCode, string number = ValidCard) =>
        new(planCode, "Olivia Owner", number, 12, DateTimeOffset.UtcNow.Year + 2, "123");

    /// <summary>Moves a workspace to another plan through the simulated checkout.</summary>
    public static async Task ChangePlanAsync(this HttpClient ownerClient, string planCode)
    {
        var response = await ownerClient.PostAsJsonAsync("/api/v1/billing/checkout", Card(planCode), TestJson.Options);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Runs something inside one workspace, the way a background job would.</summary>
    public static async Task<T> InTenantAsync<T>(this ApiFactory factory, Guid tenantId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<NexDesk.Application.Common.ITenantScopeFactory>().Begin(tenantId);
        return await work(scope.ServiceProvider);
    }
}
