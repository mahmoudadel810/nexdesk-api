using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Common;
using NexDesk.Application.Platform;
using NexDesk.Application.Reports;
using NexDesk.Application.Tickets;
using NexDesk.Infrastructure.Seeding;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Seeding;

[Collection(ApiCollection.Name)]
public sealed class DemoSeederTests(ApiFactory factory)
{
    private const string Password = "NexDesk-demo-2026";

    [Fact]
    public async Task The_seeded_demo_can_be_signed_into_and_fills_every_screen_without_sending_anything()
    {
        var emailsBefore = factory.Emails.All.Count;
        await using var seeding = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(s => s.AddDemoSeeding()));

        var first = await SeedAsync(seeding);
        var second = await SeedAsync(seeding);

        first.Seeded.ShouldBeTrue(first.Message);
        second.Seeded.ShouldBeFalse();
        factory.Emails.All.Count.ShouldBe(emailsBefore);

        var owner = await StaffAsync("sarah@brightpath.demo");
        var tickets = await owner.GetAsync<PagedResult<TicketListItem>>("/api/v1/tickets?pageSize=100&status=New&status=Open&status=Pending&status=OnHold&status=Solved&status=Closed");
        tickets.TotalCount.ShouldBeGreaterThan(3);
        tickets.Items.ShouldContain(t => t.Status == NexDesk.Domain.Tickets.TicketStatus.Closed);

        var subject = tickets.Items[0].Subject.Split(' ')[^1];
        (await owner.GetAsync<PagedResult<TicketListItem>>($"/api/v1/tickets?search={Uri.EscapeDataString(subject)}"))
            .TotalCount.ShouldBeGreaterThan(0);
        var overview = await owner.GetAsync<OverviewDto>("/api/v1/reports/overview");
        overview.ShouldNotBeNull();

        // New tickets carry on from the seeded numbering instead of colliding with it.
        var next = await owner.CreateTicketAsync(TestTickets.NewTicket("After the seed"));
        next.Number.ShouldBe(tickets.Items.Max(t => t.Number) + 1);

        foreach (var email in new[] { "maya@brightpath.demo", "finance@brightpath.demo", "layla@souqna.demo" })
        {
            (await StaffAsync(email)).ShouldNotBeNull();
        }

        var customer = factory.CreateBareClient();
        var portal = await customer.PostAsJsonAsync(
            "/api/v1/portal/brightpath/auth/login", new { email = "casey@customers.demo", password = Password });
        portal.StatusCode.ShouldBe(HttpStatusCode.OK, await portal.Content.ReadAsStringAsync());

        var admin = await StaffAsync("admin@nexdesk.demo");
        var workspaces = await admin.GetAsync<PagedResult<AdminTenantListItem>>("/api/v1/admin/tenants?search=demo&pageSize=100");
        workspaces.Items.Select(t => t.Slug).ShouldContain("northwind-demo");
        var metrics = await admin.GetAsync<PlatformMetrics>("/api/v1/admin/metrics");
        metrics.Mrr[^1].Value.ShouldBeGreaterThan(0);
    }

    private static async Task<SeedResult> SeedAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(SeedSize.Small, CancellationToken.None);
    }

    private async Task<HttpClient> StaffAsync(string email)
    {
        var client = factory.CreateBareClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password));
        var auth = await response.ReadAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }
}
