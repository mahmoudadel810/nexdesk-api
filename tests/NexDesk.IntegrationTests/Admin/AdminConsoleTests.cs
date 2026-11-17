using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Audit;
using NexDesk.Application.Common;
using NexDesk.Application.Platform;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Common;
using NexDesk.Domain.Developer;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Admin;

[Collection(ApiCollection.Name)]
public sealed class AdminConsoleTests(ApiFactory factory)
{
    private const string Tenants = "/api/v1/admin/tenants";

    [Fact]
    public async Task The_platform_admin_signs_in_without_a_workspace_and_staff_cannot_reach_the_console()
    {
        var admin = await AdminAsync();
        var session = await admin.GetAsync<NexDesk.Application.Auth.SessionDto>("/api/v1/auth/me");
        session.Tenant.ShouldBeNull();
        session.User.IsPlatformAdmin.ShouldBeTrue();

        var owner = await factory.CreateWorkspaceAsync();
        foreach (var url in new[] { Tenants, $"{Tenants}/{owner.TenantId}", "/api/v1/admin/metrics" })
        {
            (await owner.Client.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await owner.Client.PostAsJsonAsync($"{Tenants}/{owner.TenantId}/suspend", new SuspendTenantRequest()))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await factory.CreateBareClient().GetAsync(Tenants)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The admin belongs to no workspace, so workspace screens have nothing to show them.
        (await admin.GetAsync("/api/v1/tickets")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Every_workspace_is_listed_with_its_plan_team_and_owner_and_can_be_searched()
    {
        var admin = await AdminAsync();
        var acme = await factory.CreateWorkspaceAsync($"Zeta {Guid.NewGuid():N}"[..20]);
        await acme.Client.ChangePlanAsync(Plan.Pro);
        await factory.AddMemberAsync(acme, Role.Agent);
        await factory.AddMemberAsync(acme, Role.Viewer);
        await acme.Client.CreateTicketAsync(TestTickets.NewTicket());

        var found = (await admin.GetAsync<PagedResult<AdminTenantListItem>>($"{Tenants}?search={acme.Slug}"))
            .Items.ShouldHaveSingleItem();
        found.ShouldBe(new AdminTenantListItem(
            acme.TenantId, found.Name, acme.Slug, TenantStatus.Active, Plan.Pro, "Pro", 2900, 2, 1, acme.Email, found.CreatedAt));
        (await admin.GetAsync<PagedResult<AdminTenantListItem>>($"{Tenants}?search={acme.Email.ToUpperInvariant()}"))
            .Items.ShouldHaveSingleItem().Id.ShouldBe(acme.TenantId);

        var onPro = await admin.GetAsync<PagedResult<AdminTenantListItem>>($"{Tenants}?plan=pro&pageSize=100");
        onPro.Items.ShouldAllBe(t => t.PlanCode == Plan.Pro);
        var newest = await admin.GetAsync<PagedResult<AdminTenantListItem>>($"{Tenants}?pageSize=5");
        newest.Items.Select(t => t.CreatedAt).ShouldBeInOrder(SortDirection.Descending);
        (await admin.GetAsync($"{Tenants}?pageSize=1000")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_workspace_overview_shows_usage_against_its_limits_admins_and_invoices()
    {
        var admin = await AdminAsync();
        var workspace = await factory.CreateWorkspaceAsync();
        await workspace.Client.ChangePlanAsync(Plan.Business);
        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket());
        await workspace.Client.PostAsJsonAsync("/api/v1/api-keys", new IssueApiKeyRequest("Sync", [ApiScopes.TicketsRead]));

        var detail = await admin.GetAsync<AdminTenantDetail>($"{Tenants}/{workspace.TenantId}");

        (detail.Slug, detail.Status, detail.PlanCode, detail.MonthlyPriceCents).ShouldBe((workspace.Slug, TenantStatus.Active, Plan.Business, 9900));
        (detail.Usage.Agents, detail.Usage.TicketsThisMonth, detail.Usage.ActiveApiKeys).ShouldBe((1, 1, 1));
        (detail.Totals.Tickets, detail.Totals.OpenTickets, detail.Totals.Customers).ShouldBe((1, 1, 1));
        detail.Admins.ShouldHaveSingleItem().Email.ShouldBe(workspace.Email);
        detail.RecentInvoices.ShouldHaveSingleItem().PlanName.ShouldBe("Business");
        (await admin.GetAsync($"{Tenants}/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Suspending_a_workspace_locks_out_its_team_and_integrations_at_once_and_reactivating_restores_them()
    {
        var admin = await AdminAsync();
        var workspace = await BusinessWorkspaceAsync();
        var key = (await (await workspace.Client.PostAsJsonAsync(
            "/api/v1/api-keys", new IssueApiKeyRequest("Sync", [ApiScopes.TicketsRead]))).ReadAsync<NexDesk.Application.Developer.IssuedApiKeyDto>()).Key;
        var integration = factory.CreateBareClient();
        integration.DefaultRequestHeaders.Add("X-Api-Key", key);
        (await workspace.Client.GetAsync("/api/v1/tickets")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var suspended = await (await admin.PostAsJsonAsync($"{Tenants}/{workspace.TenantId}/suspend", new SuspendTenantRequest(" Unpaid invoices ")))
            .ReadAsync<AdminTenantDetail>();

        (suspended.Status, suspended.SuspensionReason).ShouldBe((TenantStatus.Suspended, "Unpaid invoices"));
        suspended.SuspendedAt.ShouldNotBeNull();
        var refused = await workspace.Client.GetAsync("/api/v1/tickets");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ProblemCodeAsync()).ShouldBe("tenant-suspended");
        (await integration.GetAsync("/api/v1/public/tickets")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await factory.CreateBareClient().GetAsync($"/api/v1/portal/{workspace.Slug}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var login = await factory.CreateBareClient().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(workspace.Email, workspace.Password));
        (await login.ProblemCodeAsync()).ShouldBe("tenant-suspended");

        var reactivated = await (await admin.PostAsync($"{Tenants}/{workspace.TenantId}/reactivate", null)).ReadAsync<AdminTenantDetail>();
        (reactivated.Status, reactivated.SuspendedAt, reactivated.SuspensionReason).ShouldBe((TenantStatus.Active, null, null));
        (await workspace.Client.GetAsync("/api/v1/tickets")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await integration.GetAsync("/api/v1/public/tickets")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The workspace sees who closed it: NexDesk, not a person on its team.
        var audit = await workspace.Client.GetAsync<PagedResult<AuditLogDto>>("/api/v1/audit-logs?entityType=workspace&operation=Updated");
        audit.Items.Count.ShouldBe(2);
        audit.Items.ShouldAllBe(e => e.Actor.Type == ActorType.System);
        audit.Items[1].Changes.ShouldContain(new NexDesk.Domain.Audit.AuditChange("status", "Active", "Suspended"));
    }

    [Fact]
    public async Task Metrics_count_workspaces_revenue_by_plan_and_sign_ups_per_month()
    {
        var admin = await AdminAsync();
        var before = await admin.GetAsync<PlatformMetrics>("/api/v1/admin/metrics?months=3");
        var paying = await factory.CreateWorkspaceAsync();
        await paying.Client.ChangePlanAsync(Plan.Pro);

        var after = await admin.GetAsync<PlatformMetrics>("/api/v1/admin/metrics?months=3");

        after.Overview.Workspaces.ShouldBe(before.Overview.Workspaces + 1);
        after.Overview.MrrCents.ShouldBe(before.Overview.MrrCents + 2900);
        after.Overview.Plans.Single(p => p.PlanCode == Plan.Pro).Workspaces
            .ShouldBe(before.Overview.Plans.Single(p => p.PlanCode == Plan.Pro).Workspaces + 1);
        after.Mrr.Count.ShouldBe(3);
        after.Mrr[^1].Month.ShouldBe(new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1));
        after.Mrr[^1].Value.ShouldBe(before.Mrr[^1].Value + 2900);
        after.Signups[^1].Value.ShouldBe(before.Signups[^1].Value + 1);
        (await admin.GetAsync("/api/v1/admin/metrics?months=0")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_job_dashboard_opens_only_through_a_single_use_link_from_the_admin()
    {
        var admin = await AdminAsync();
        var browser = factory.CreateBareClient();
        (await browser.GetAsync("/hangfire")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var link = await (await admin.PostAsync("/api/v1/admin/jobs-dashboard", null)).ReadAsync<JobsDashboardLink>();
        var opened = await browser.GetAsync(link.Url);
        opened.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var cookie = opened.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("nexdesk_jobs=", StringComparison.Ordinal));
        cookie.ShouldContain("httponly");
        cookie.ShouldContain("path=/hangfire");

        var dashboard = new HttpRequestMessage(HttpMethod.Get, "/hangfire");
        dashboard.Headers.Add("Cookie", cookie.Split(';')[0]);
        (await browser.SendAsync(dashboard)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.GetAsync(link.Url)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var owner = await factory.CreateWorkspaceAsync();
        (await owner.Client.PostAsync("/api/v1/admin/jobs-dashboard", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = factory.CreateBareClient();
        var auth = await (await client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest(ApiFactory.PlatformAdminEmail, ApiFactory.PlatformAdminPassword)))
            .ReadAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private async Task<TestWorkspace> BusinessWorkspaceAsync()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await workspace.Client.ChangePlanAsync(Plan.Business);
        return workspace;
    }
}
