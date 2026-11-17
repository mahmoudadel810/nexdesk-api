using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Billing;
using NexDesk.Application.Common;
using NexDesk.Application.Users;
using NexDesk.Domain.Automation;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;
using NexDesk.IntegrationTests.Infrastructure;
using NexDesk.IntegrationTests.Portal;

namespace NexDesk.IntegrationTests.Billing;

[Collection(ApiCollection.Name)]
public sealed class BillingTests(ApiFactory factory)
{
    private const string Checkout = "/api/v1/billing/checkout";

    private static Task<HttpResponseMessage> Invite(HttpClient client, Role role) =>
        client.PostAsJsonAsync(
            "/api/v1/invitations", new CreateInvitationRequest(TestWorkspaces.UniqueEmail("invite"), role), TestJson.Options);

    private static async Task ShouldHitLimit(Task<HttpResponseMessage> request, string code)
    {
        var response = await request;
        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired, await response.Content.ReadAsStringAsync());
        (await response.ProblemCodeAsync()).ShouldBe(code);
    }

    private static Task<HttpResponseMessage> AddRule(HttpClient client, string name) =>
        client.PostAsJsonAsync(
            "/api/v1/automation/rules",
            new SaveAutomationRuleRequest(
                name,
                AutomationTrigger.TicketCreated,
                new RuleDefinition(ConditionMatch.All, [], [new RuleAction(RuleActionType.AddTag, "x")]),
                IsEnabled: false),
            TestJson.Options);

    [Fact]
    public async Task Anyone_can_read_the_plans_and_a_new_workspace_starts_on_the_free_one()
    {
        var plans = await factory.CreateBareClient().GetAsync<List<PlanDto>>("/api/v1/billing/plans");
        plans.Select(p => (p.Code, p.PriceCentsMonthly, p.MaxAgents, p.ApiAccess)).ShouldBe(
        [
            (Plan.Free, 0, 3, false),
            (Plan.Pro, 2900, 10, false),
            (Plan.Business, 9900, null, true),
        ]);

        var workspace = await factory.CreateWorkspaceAsync();
        var subscription = await workspace.Client.GetAsync<SubscriptionDto>("/api/v1/billing/subscription");
        subscription.Plan.Code.ShouldBe(Plan.Free);
        subscription.CurrentPeriodEnd.ShouldBeNull();
        subscription.CardLast4.ShouldBeNull();

        await workspace.Client.CreateTicketAsync();
        var usage = await workspace.Client.GetAsync<UsageDto>("/api/v1/billing/usage");
        usage.ShouldBe(new UsageDto(1, 3, 1, 100, 0, 1024L * 1024 * 1024, 0, 3, false));
        (await workspace.Client.GetAsync<PagedResult<InvoiceDto>>("/api/v1/billing/invoices")).TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Upgrading_takes_a_simulated_payment_and_produces_a_paid_invoice_with_a_pdf()
    {
        var workspace = await factory.CreateWorkspaceAsync("Acme Billing");

        foreach (var (request, status, code) in new[]
        {
            (TestBilling.Card("pro", "4242 4242 4242 4241"), HttpStatusCode.BadRequest, "invalid-card"),
            (TestBilling.Card("pro") with { ExpiryYear = 2020 }, HttpStatusCode.BadRequest, "invalid-card"),
            (TestBilling.Card("pro") with { Cvc = null }, HttpStatusCode.BadRequest, "invalid-card"),
            (new CheckoutRequest("pro"), HttpStatusCode.BadRequest, "invalid-card"),
            (TestBilling.Card("pro", PaymentCard.DeclinedNumber), HttpStatusCode.Conflict, "card-declined"),
            (TestBilling.Card("enterprise"), HttpStatusCode.NotFound, "plan-not-found"),
            (new CheckoutRequest("free"), HttpStatusCode.Conflict, "already-on-plan"),
        })
        {
            var refused = await workspace.Client.PostAsJsonAsync(Checkout, request, TestJson.Options);
            refused.StatusCode.ShouldBe(status, await refused.Content.ReadAsStringAsync());
            (await refused.ProblemCodeAsync()).ShouldBe(code);
        }

        (await workspace.Client.GetAsync<PagedResult<InvoiceDto>>("/api/v1/billing/invoices")).TotalCount.ShouldBe(0);

        var paid = await (await workspace.Client.PostAsJsonAsync(Checkout, TestBilling.Card("pro"), TestJson.Options))
            .ReadAsync<SubscriptionDto>();
        paid.Plan.Code.ShouldBe(Plan.Pro);
        (paid.CardBrand, paid.CardLast4).ShouldBe(("Visa", "4242"));
        paid.CurrentPeriodEnd!.Value.ShouldBe(paid.CurrentPeriodStart.AddMonths(1));
        paid.CurrentPeriodStart.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        var invoice = (await workspace.Client.GetAsync<PagedResult<InvoiceDto>>("/api/v1/billing/invoices"))
            .Items.ShouldHaveSingleItem();
        (invoice.Number, invoice.PlanName, invoice.AmountCents, invoice.Currency, invoice.Reason, invoice.CardLast4)
            .ShouldBe(("INV-00001", "Pro", 2900, "USD", InvoiceReason.PlanChange, "4242"));
        invoice.PaidAt.ShouldBe(invoice.IssuedAt);

        var pdf = await workspace.Client.GetAsync($"/api/v1/billing/invoices/{invoice.Id}/pdf");
        pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        pdf.Content.Headers.ContentDisposition!.FileName.ShouldBe("INV-00001.pdf");
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).ShouldBe("%PDF-");
        bytes.Length.ShouldBeGreaterThan(2000);

        // Moving up again bills the new plan; moving back to free costs nothing and ends the paid period.
        await workspace.Client.ChangePlanAsync(Plan.Business);
        var free = await (await workspace.Client.PostAsJsonAsync(Checkout, new CheckoutRequest("free"), TestJson.Options))
            .ReadAsync<SubscriptionDto>();
        (free.Plan.Code, free.CurrentPeriodEnd, free.CardLast4).ShouldBe((Plan.Free, null, null));
        var invoices = await workspace.Client.GetAsync<PagedResult<InvoiceDto>>("/api/v1/billing/invoices");
        invoices.Items.Select(i => (i.Number, i.AmountCents)).ShouldBe([("INV-00002", 9900), ("INV-00001", 2900)]);
    }

    [Fact]
    public async Task The_free_plan_stops_at_three_people_who_work_on_tickets()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var viewer = await factory.AddMemberAsync(workspace, Role.Viewer, "Vera Viewer");
        (await Invite(workspace.Client, Role.Admin)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // The owner, one agent and one pending invitation fill the three seats. Viewers are free.
        await ShouldHitLimit(Invite(workspace.Client, Role.Agent), "plan-limit-agents");
        (await Invite(workspace.Client, Role.Viewer)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await ShouldHitLimit(
            workspace.Client.PutAsJsonAsync($"/api/v1/users/{viewer.UserId}/role", new ChangeRoleRequest(Role.Agent), TestJson.Options),
            "plan-limit-agents");

        // A deactivated agent frees a seat, and cannot come back once somebody else took it.
        (await workspace.Client.PostAsync($"/api/v1/users/{agent.UserId}/deactivate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Invite(workspace.Client, Role.Agent)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await ShouldHitLimit(workspace.Client.PostAsync($"/api/v1/users/{agent.UserId}/reactivate", null), "plan-limit-agents");
        (await workspace.Client.GetAsync<UsageDto>("/api/v1/billing/usage")).Agents.ShouldBe(3);

        await workspace.Client.ChangePlanAsync(Plan.Pro);
        (await workspace.Client.PostAsync($"/api/v1/users/{agent.UserId}/reactivate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await workspace.Client.PutAsJsonAsync(
            $"/api/v1/users/{viewer.UserId}/role", new ChangeRoleRequest(Role.Agent), TestJson.Options))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Invite(workspace.Client, Role.Agent)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Six people no longer fit the free plan.
        var back = await workspace.Client.PostAsJsonAsync(Checkout, new CheckoutRequest("free"), TestJson.Options);
        back.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await back.ProblemCodeAsync()).ShouldBe("downgrade-agents");
        (await workspace.Client.GetAsync<SubscriptionDto>("/api/v1/billing/subscription")).Plan.Code.ShouldBe(Plan.Pro);
    }

    [Fact]
    public async Task The_monthly_ticket_allowance_applies_to_every_channel_and_deleting_does_not_refill_it()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var customer = await factory.SignUpCustomerAsync(workspace);
        Guid last = default;
        for (var number = 1; number <= 100; number++)
        {
            last = (await workspace.Client.CreateTicketAsync()).Id;
        }

        (await workspace.Client.DeleteAsync($"/api/v1/tickets/{last}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workspace.Client.GetAsync<UsageDto>("/api/v1/billing/usage")).TicketsThisMonth.ShouldBe(100);

        await ShouldHitLimit(
            workspace.Client.PostAsJsonAsync("/api/v1/tickets", TestTickets.NewTicket(), TestJson.Options), "plan-limit-tickets");
        await ShouldHitLimit(
            customer.Client.PostAsJsonAsync(workspace.PortalPath("/tickets"), new PortalTicketRequest("Help", "Please.")),
            "plan-limit-tickets");
        await ShouldHitLimit(
            factory.SendInboundAsync(workspace.Slug, new InboundEmailRequest(customer.Email, "Casey", "Help", "Please.")),
            "plan-limit-tickets");

        await workspace.Client.ChangePlanAsync(Plan.Pro);
        await workspace.Client.CreateTicketAsync();
        (await workspace.Client.GetAsync<UsageDto>("/api/v1/billing/usage")).TicketsThisMonth.ShouldBe(101);
    }

    [Fact]
    public async Task Automation_rules_and_storage_are_capped_by_the_plan()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        foreach (var name in new[] { "One", "Two", "Three" })
        {
            (await AddRule(workspace.Client, name)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        await ShouldHitLimit(AddRule(workspace.Client, "Four"), "plan-limit-automation");
        await workspace.Client.ChangePlanAsync(Plan.Pro);
        (await AddRule(workspace.Client, "Four")).StatusCode.ShouldBe(HttpStatusCode.Created);
        var blocked = await workspace.Client.PostAsJsonAsync(Checkout, new CheckoutRequest("free"), TestJson.Options);
        (await blocked.ProblemCodeAsync()).ShouldBe("downgrade-automation");

        // Fill the workspace's storage to just under the Pro allowance without uploading 20 GB.
        const long ProStorage = 20L * 1024 * 1024 * 1024;
        await factory.InTenantAsync(workspace.TenantId, async services =>
        {
            var db = services.GetRequiredService<IAppDbContext>();
            db.Attachments.Add(Attachment.Create(
                workspace.TenantId, "archive.zip", "application/zip", ProStorage - 10, Actor.Staff(workspace.OwnerId), DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync(CancellationToken.None);
        });

        (await workspace.Client.GetAsync<UsageDto>("/api/v1/billing/usage")).StorageBytes.ShouldBe(ProStorage - 10);
        (await workspace.Client.PostAsync("/api/v1/attachments", TestTickets.FileForm("ok.txt", new byte[10])))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await ShouldHitLimit(
            workspace.Client.PostAsync("/api/v1/attachments", TestTickets.FileForm("more.txt", new byte[1])), "plan-limit-storage");

        await workspace.Client.ChangePlanAsync(Plan.Business);
        (await workspace.Client.PostAsync("/api/v1/attachments", TestTickets.FileForm("more.txt", new byte[1])))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var tooBig = await workspace.Client.PostAsJsonAsync(Checkout, TestBilling.Card("pro"), TestJson.Options);
        (await tooBig.ProblemCodeAsync()).ShouldBe("downgrade-storage");
    }

    [Fact]
    public async Task A_paid_subscription_renews_itself_with_a_new_invoice_each_month()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var free = await factory.CreateWorkspaceAsync();
        await workspace.Client.ChangePlanAsync(Plan.Pro);
        var start = (await workspace.Client.GetAsync<SubscriptionDto>("/api/v1/billing/subscription")).CurrentPeriodStart;

        Task<bool> RenewAt(Guid tenantId, DateTimeOffset now) => factory.InTenantAsync(
            tenantId, services => services.GetRequiredService<BillingService>().RenewIfDueAsync(now, CancellationToken.None));

        (await RenewAt(workspace.TenantId, start.AddDays(20))).ShouldBeFalse();
        (await RenewAt(free.TenantId, start.AddMonths(3))).ShouldBeFalse();
        (await RenewAt(workspace.TenantId, start.AddMonths(1).AddHours(1))).ShouldBeTrue();
        (await RenewAt(workspace.TenantId, start.AddMonths(1).AddHours(2))).ShouldBeFalse();

        var subscription = await workspace.Client.GetAsync<SubscriptionDto>("/api/v1/billing/subscription");
        subscription.CurrentPeriodStart.ShouldBe(start.AddMonths(1), TimeSpan.FromSeconds(1));
        subscription.CurrentPeriodEnd!.Value.ShouldBe(start.AddMonths(2), TimeSpan.FromSeconds(1));
        var invoices = (await workspace.Client.GetAsync<PagedResult<InvoiceDto>>("/api/v1/billing/invoices")).Items;
        invoices.Select(i => (i.Number, i.Reason, i.AmountCents)).ShouldBe(
            [("INV-00002", InvoiceReason.Renewal, 2900), ("INV-00001", InvoiceReason.PlanChange, 2900)]);
        invoices[0].PeriodStart.ShouldBe(start.AddMonths(1), TimeSpan.FromSeconds(1));
        (await free.Client.GetAsync<PagedResult<InvoiceDto>>("/api/v1/billing/invoices")).TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_scheduled_jobs_renew_what_is_due_and_remove_only_what_is_stale()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var bystander = await factory.CreateWorkspaceAsync();
        await workspace.Client.ChangePlanAsync(Plan.Pro);
        await bystander.Client.ChangePlanAsync(Plan.Pro);
        var rule = await (await AddRule(workspace.Client, "Keep me")).ReadAsync<NexDesk.Application.Automation.AutomationRuleDto>();
        (await workspace.Client.PostAsync($"/api/v1/automation/rules/{rule.Id}/enable", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var ticket = await workspace.Client.CreateTicketAsync();
        var expired = TestWorkspaces.UniqueEmail("expired");
        (await workspace.Client.PostAsJsonAsync(
            "/api/v1/invitations", new CreateInvitationRequest(expired, Role.Viewer), TestJson.Options))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        // Age the data: the paid period ended, the invitation lapsed long ago, and the rule ran on the
        // ticket twice more over the past year.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexDesk.Infrastructure.Persistence.AppDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE subscriptions SET current_period_end = now() - interval '1 hour' WHERE tenant_id = {workspace.TenantId}");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE invitations SET expires_at = now() - interval '30 days' WHERE email = {expired}");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO automation_executions (id, tenant_id, rule_id, ticket_id, ticket_number, ticket_subject, outcome, steps, created_at)
                SELECT gen_random_uuid(), tenant_id, rule_id, ticket_id, ticket_number, ticket_subject, outcome, steps, now() - age
                FROM automation_executions, (VALUES (interval '300 days'), (interval '200 days')) AS old(age)
                WHERE rule_id = {rule.Id}
                """);
        }

        Task<PagedResult<NexDesk.Application.Automation.AutomationExecutionDto>> Log() =>
            workspace.Client.GetAsync<PagedResult<NexDesk.Application.Automation.AutomationExecutionDto>>(
                $"/api/v1/automation/rules/{rule.Id}/executions");
        (await Log()).TotalCount.ShouldBe(3);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NexDesk.Infrastructure.Billing.SubscriptionRenewalJob>()
                .RunAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<NexDesk.Infrastructure.Billing.CleanupJob>()
                .RunAsync(CancellationToken.None);
        }

        // The due subscription was billed again, the other one was not.
        var invoices = (await workspace.Client.GetAsync<PagedResult<InvoiceDto>>("/api/v1/billing/invoices")).Items;
        invoices.Select(i => i.Reason).ShouldBe([InvoiceReason.Renewal, InvoiceReason.PlanChange]);
        (await workspace.Client.GetAsync<SubscriptionDto>("/api/v1/billing/subscription"))
            .CurrentPeriodEnd!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(20));
        (await bystander.Client.GetAsync<PagedResult<InvoiceDto>>("/api/v1/billing/invoices")).TotalCount.ShouldBe(1);

        // Old log entries go, except that the newest one for the ticket always stays.
        var log = await Log();
        log.Items.ShouldHaveSingleItem().TicketId.ShouldBe(ticket.Id);
        log.Items[0].CreatedAt.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        // The lapsed invitation is gone; live sessions are untouched.
        (await workspace.Client.GetAsync<List<InvitationDto>>("/api/v1/invitations")).ShouldNotContain(i => i.Email == expired);
        (await factory.CreateBareClient().RefreshAsync(workspace.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reviving_an_expired_invitation_needs_a_free_seat()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var lapsed = TestWorkspaces.UniqueEmail("lapsed");
        var invitation = await (await workspace.Client.PostAsJsonAsync(
            "/api/v1/invitations", new CreateInvitationRequest(lapsed, Role.Agent), TestJson.Options)).ReadAsync<InvitationDto>();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NexDesk.Infrastructure.Persistence.AppDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE invitations SET expires_at = now() - interval '1 day' WHERE email = {lapsed}");
        }

        // While it was expired, two other people took the remaining seats.
        (await Invite(workspace.Client, Role.Agent)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Invite(workspace.Client, Role.Agent)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await ShouldHitLimit(workspace.Client.PostAsync($"/api/v1/invitations/{invitation.Id}/resend", null), "plan-limit-agents");

        await workspace.Client.ChangePlanAsync(Plan.Pro);
        (await workspace.Client.PostAsync($"/api/v1/invitations/{invitation.Id}/resend", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Only_the_owner_pays_and_invoices_never_cross_workspaces()
    {
        var acme = await factory.CreateWorkspaceAsync();
        var globex = await factory.CreateWorkspaceAsync();
        var admin = await factory.AddMemberAsync(acme, Role.Admin, "Adam Admin");
        var agent = await factory.AddMemberAsync(acme, Role.Agent);
        await acme.Client.ChangePlanAsync(Plan.Pro);
        await globex.Client.ChangePlanAsync(Plan.Business);
        var invoice = (await acme.Client.GetAsync<PagedResult<InvoiceDto>>("/api/v1/billing/invoices")).Items.Single();

        (await admin.Client.GetAsync<UsageDto>("/api/v1/billing/usage")).MaxAgents.ShouldBe(10);
        (await admin.Client.GetAsync<SubscriptionDto>("/api/v1/billing/subscription")).Plan.Code.ShouldBe(Plan.Pro);
        (await admin.Client.PostAsJsonAsync(Checkout, TestBilling.Card("business"), TestJson.Options))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.Client.GetAsync("/api/v1/billing/invoices")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.Client.GetAsync($"/api/v1/billing/invoices/{invoice.Id}/pdf")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.GetAsync("/api/v1/billing/usage")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await factory.CreateBareClient().GetAsync("/api/v1/billing/subscription")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Each workspace numbers its own invoices and sees only those.
        var theirs = (await globex.Client.GetAsync<PagedResult<InvoiceDto>>("/api/v1/billing/invoices")).Items.ShouldHaveSingleItem();
        (theirs.Number, theirs.PlanCode).ShouldBe(("INV-00001", Plan.Business));
        (await globex.Client.GetAsync($"/api/v1/billing/invoices/{invoice.Id}/pdf")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await acme.Client.GetAsync<SubscriptionDto>("/api/v1/billing/subscription")).Plan.Code.ShouldBe(Plan.Pro);
        (await acme.Client.GetAsync<UsageDto>("/api/v1/billing/usage")).ApiAccess.ShouldBeFalse();
        (await globex.Client.GetAsync<UsageDto>("/api/v1/billing/usage")).ApiAccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Requests_are_limited_per_address_for_sign_in_and_per_plan_for_a_workspace()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await using var limited = factory.WithWebHostBuilder(builder => builder
            .UseSetting("RateLimiting:Enabled", "true")
            .UseSetting("RateLimiting:AuthPerMinute", "3"));
        var options = new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false };

        var visitor = limited.CreateClient(options);
        var wrong = new LoginRequest(workspace.Email, "not-the-password");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            (await visitor.PostAsJsonAsync("/api/v1/auth/login", wrong)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var throttled = await visitor.PostAsJsonAsync("/api/v1/auth/login", wrong);
        throttled.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await throttled.ProblemCodeAsync()).ShouldBe("too-many-requests");
        int.Parse(throttled.Headers.GetValues("Retry-After").Single()).ShouldBeInRange(1, 60);
        (await visitor.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Refreshing a session is not a guess at a password, so it keeps working for the same address.
        (await visitor.RefreshAsync(workspace.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Every team member gets what the workspace's plan allows per minute.
        var plans = await factory.CreateBareClient().GetAsync<List<PlanDto>>("/api/v1/billing/plans");
        var freeLimit = plans.Single(p => p.Code == Plan.Free).RequestsPerMinute;
        var staff = limited.CreateClient(options);
        staff.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", workspace.AccessToken);
        for (var request = 1; request <= freeLimit; request++)
        {
            (await staff.GetAsync("/api/v1/billing/usage")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await staff.GetAsync("/api/v1/billing/usage")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // A colleague has an allowance of their own, and so does every other workspace.
        var colleague = await factory.AddMemberAsync(workspace, Role.Admin, "Adam Admin");
        var colleagueClient = limited.CreateClient(options);
        colleagueClient.DefaultRequestHeaders.Authorization = colleague.Client.DefaultRequestHeaders.Authorization;
        (await colleagueClient.GetAsync("/api/v1/billing/usage")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A bigger plan raises the allowance from the next request on.
        var other = await factory.CreateWorkspaceAsync();
        var otherStaff = limited.CreateClient(options);
        otherStaff.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        (await otherStaff.GetAsync("/api/v1/billing/usage")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await otherStaff.ChangePlanAsync(Plan.Pro);
        for (var request = 1; request <= freeLimit + 10; request++)
        {
            (await otherStaff.GetAsync("/api/v1/billing/usage")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await staff.GetAsync("/api/v1/billing/usage")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
