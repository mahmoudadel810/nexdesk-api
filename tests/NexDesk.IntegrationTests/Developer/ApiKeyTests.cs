using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Audit;
using NexDesk.Application.Common;
using NexDesk.Application.Developer;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Common;
using NexDesk.Domain.Developer;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Developer;

[Collection(ApiCollection.Name)]
public sealed class ApiKeyTests(ApiFactory factory)
{
    private const string Keys = "/api/v1/api-keys";
    private const string PublicTickets = "/api/v1/public/tickets";

    [Fact]
    public async Task Keys_need_a_plan_with_api_access()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        var refused = await workspace.Client.PostAsJsonAsync(Keys, new IssueApiKeyRequest("Zapier", [ApiScopes.TicketsRead]));

        refused.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        (await refused.ProblemCodeAsync()).ShouldBe("plan-feature-api");
    }

    [Fact]
    public async Task An_issued_key_is_shown_once_and_opens_tickets_on_the_api_channel_in_its_own_name()
    {
        var workspace = await BusinessWorkspaceAsync();
        var issued = await IssueAsync(workspace, "Order sync", ApiScopes.TicketsRead, ApiScopes.TicketsWrite);

        issued.Key.ShouldStartWith("ndk_");
        issued.ApiKey.Hint.ShouldEndWith("_…");
        issued.Key.ShouldStartWith(issued.ApiKey.Hint.TrimEnd('…'));
        var listed = (await workspace.Client.GetAsync<List<ApiKeyDto>>(Keys)).ShouldHaveSingleItem();
        listed.Scopes.ShouldBe([ApiScopes.TicketsRead, ApiScopes.TicketsWrite]);
        (await workspace.Client.GetStringAsync(Keys)).ShouldNotContain(issued.Key);

        var integration = Integration(issued.Key);
        var created = await (await integration.PostAsJsonAsync(
            PublicTickets,
            new PublicCreateTicketRequest("Order 1001 arrived damaged", "Box was crushed.", "casey@example.test", "Casey"),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        created.Channel.ShouldBe(TicketChannel.Api);

        var replied = await integration.PostAsJsonAsync(
            $"{PublicTickets}/{created.Id}/messages",
            new PublicAddMessageRequest("We are sending a replacement.", SetStatus: TicketStatus.Pending),
            TestJson.Options);
        (await replied.ReadAsync<TicketDetailDto>()).Status.ShouldBe(TicketStatus.Pending);
        (await integration.GetAsync<TicketDetailDto>($"{PublicTickets}/{created.Id}")).Subject.ShouldBe("Order 1001 arrived damaged");
        (await integration.GetAsync<PagedResult<TicketListItem>>($"{PublicTickets}?search=damaged")).TotalCount.ShouldBe(1);

        var reply = (await workspace.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{created.Id}/timeline"))
            .Last(item => item.Kind == TimelineKind.Message);
        reply.Actor.ShouldBe(new ActorDto(ActorType.ApiKey, issued.ApiKey.Id, "Order sync"));

        var audited = (await workspace.Client.GetAsync<PagedResult<AuditLogDto>>($"/api/v1/audit-logs?search={created.Id}"))
            .Items.Last();
        audited.Actor.ShouldBe(new ActorDto(ActorType.ApiKey, issued.ApiKey.Id, "Order sync"));
        (await workspace.Client.GetAsync<List<ApiKeyDto>>(Keys)).Single().LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_key_can_only_do_what_its_scopes_allow()
    {
        var workspace = await BusinessWorkspaceAsync();
        var readOnly = Integration((await IssueAsync(workspace, "Dashboard", ApiScopes.TicketsRead)).Key);
        var ticket = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Existing"));

        (await readOnly.GetAsync($"{PublicTickets}/{ticket.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await readOnly.PostAsJsonAsync(PublicTickets, new PublicCreateTicketRequest("New", "Body", "a@example.test")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await readOnly.PatchAsJsonAsync($"{PublicTickets}/{ticket.Id}", new PublicUpdateTicketRequest(Priority: TicketPriority.High)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Keys_and_staff_tokens_each_open_only_their_own_door()
    {
        var workspace = await BusinessWorkspaceAsync();
        var key = (await IssueAsync(workspace, "Everything", [.. ApiScopes.All])).Key;

        // A staff token is not an API key, and an API key is not a staff session.
        (await workspace.Client.GetAsync(PublicTickets)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Integration(key).GetAsync("/api/v1/tickets")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Integration(key).GetAsync(Keys)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var noKey = factory.CreateBareClient();
        var missing = await noKey.GetAsync(PublicTickets);
        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        missing.Headers.WwwAuthenticate.ToString().ShouldContain("ApiKey");
        (await Integration(key[..^4] + "abcd").GetAsync(PublicTickets)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Integration("not-a-key").GetAsync(PublicTickets)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_revoked_key_stops_working_at_once_and_stays_listed()
    {
        var workspace = await BusinessWorkspaceAsync();
        var issued = await IssueAsync(workspace, "Old integration", ApiScopes.TicketsRead);
        var integration = Integration(issued.Key);
        (await integration.GetAsync(PublicTickets)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var revoked = await (await workspace.Client.DeleteAsync($"{Keys}/{issued.ApiKey.Id}")).ReadAsync<ApiKeyDto>();

        revoked.RevokedAt.ShouldNotBeNull();
        (await integration.GetAsync(PublicTickets)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await workspace.Client.GetAsync<List<ApiKeyDto>>(Keys)).ShouldHaveSingleItem().RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Moving_to_a_plan_without_api_access_suspends_existing_keys()
    {
        var workspace = await BusinessWorkspaceAsync();
        var integration = Integration((await IssueAsync(workspace, "Sync", ApiScopes.TicketsRead)).Key);

        await workspace.Client.ChangePlanAsync(Plan.Pro);

        var refused = await integration.GetAsync(PublicTickets);
        refused.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        (await refused.ProblemCodeAsync()).ShouldBe("plan-feature-api");
    }

    [Fact]
    public async Task Invalid_requests_are_refused_with_reasons()
    {
        var workspace = await BusinessWorkspaceAsync();

        foreach (var request in new[]
        {
            new IssueApiKeyRequest("", [ApiScopes.TicketsRead]),
            new IssueApiKeyRequest("No scopes", []),
            new IssueApiKeyRequest("Unknown scope", ["users:delete"]),
            new IssueApiKeyRequest("Too long", [ApiScopes.TicketsRead], 1000),
        })
        {
            (await workspace.Client.PostAsJsonAsync(Keys, request)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var expiring = await IssueAsync(workspace, "Trial", 30, ApiScopes.TicketsRead);
        expiring.ApiKey.ExpiresAt.ShouldNotBeNull().ShouldBeInRange(
            DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));
    }

    [Fact]
    public async Task Only_admins_and_owners_manage_keys()
    {
        var workspace = await BusinessWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var key = await IssueAsync(workspace, "Sync", ApiScopes.TicketsRead);

        (await agent.Client.GetAsync(Keys)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.PostAsJsonAsync(Keys, new IssueApiKeyRequest("Mine", [ApiScopes.TicketsRead])))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.DeleteAsync($"{Keys}/{key.ApiKey.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_key_reaches_only_its_own_workspace()
    {
        var a = await BusinessWorkspaceAsync();
        var b = await BusinessWorkspaceAsync();
        var keyOfA = await IssueAsync(a, "A's sync", [.. ApiScopes.All]);
        var ticketOfB = await b.Client.CreateTicketAsync(TestTickets.NewTicket("Private to B"));
        var integrationOfA = Integration(keyOfA.Key);

        (await integrationOfA.GetAsync($"{PublicTickets}/{ticketOfB.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await integrationOfA.PatchAsJsonAsync($"{PublicTickets}/{ticketOfB.Id}", new PublicUpdateTicketRequest(Priority: TicketPriority.Urgent)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await integrationOfA.PostAsJsonAsync($"{PublicTickets}/{ticketOfB.Id}/messages", new PublicAddMessageRequest("Hi")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await integrationOfA.GetAsync<PagedResult<TicketListItem>>(PublicTickets)).TotalCount.ShouldBe(0);
        (await b.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticketOfB.Id}")).Priority.ShouldBe(TicketPriority.Normal);

        (await b.Client.GetAsync<List<ApiKeyDto>>(Keys)).ShouldBeEmpty();
        (await b.Client.DeleteAsync($"{Keys}/{keyOfA.ApiKey.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await a.Client.GetAsync<List<ApiKeyDto>>(Keys)).ShouldHaveSingleItem().RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Each_key_gets_the_plans_allowance_per_minute()
    {
        var workspace = await BusinessWorkspaceAsync();
        var first = await IssueAsync(workspace, "First", ApiScopes.TicketsRead);
        var second = await IssueAsync(workspace, "Second", ApiScopes.TicketsRead);
        await using var limited = factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:Enabled", "true"));
        var plans = await factory.CreateBareClient().GetAsync<List<NexDesk.Application.Billing.PlanDto>>("/api/v1/billing/plans");
        var allowance = plans.Single(p => p.Code == Plan.Business).RequestsPerMinute;

        var client = Integration(first.Key, limited);
        var missing = $"{PublicTickets}/{Guid.NewGuid()}";
        for (var request = 1; request <= allowance; request++)
        {
            (await client.GetAsync(missing)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        (await client.GetAsync(missing)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await Integration(second.Key, limited).GetAsync(missing)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<TestWorkspace> BusinessWorkspaceAsync()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await workspace.Client.ChangePlanAsync(Plan.Business);
        return workspace;
    }

    private static Task<IssuedApiKeyDto> IssueAsync(TestWorkspace workspace, string name, params string[] scopes) =>
        IssueAsync(workspace, name, null, scopes);

    private static async Task<IssuedApiKeyDto> IssueAsync(TestWorkspace workspace, string name, int? days, params string[] scopes) =>
        await (await workspace.Client.PostAsJsonAsync(Keys, new IssueApiKeyRequest(name, scopes, days)))
            .ReadAsync<IssuedApiKeyDto>();

    private HttpClient Integration(string key, WebApplicationFactory<Program>? host = null)
    {
        var client = (host ?? factory).CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
