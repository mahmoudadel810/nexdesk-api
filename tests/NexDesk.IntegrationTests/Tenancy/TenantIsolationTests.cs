using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Application.Common;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Tenancy;

[Collection(ApiCollection.Name)]
public sealed class TenantIsolationTests(ApiFactory factory)
{
    [Fact]
    public async Task Each_session_sees_only_its_own_workspace()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();

        var meA = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        var meB = await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");

        meA.GetProperty("tenant").GetProperty("id").GetGuid().ShouldBe(a.TenantId);
        meB.GetProperty("tenant").GetProperty("id").GetGuid().ShouldBe(b.TenantId);
        meA.GetProperty("user").GetProperty("id").GetGuid().ShouldNotBe(meB.GetProperty("user").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_query_filter_hides_users_of_other_tenants()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();

        (await db.Users.CountAsync()).ShouldBe(0);

        using (tenantScopes.Begin(a.TenantId))
        {
            var visible = await db.Users.AsNoTracking().Select(u => u.Id).ToListAsync();
            visible.ShouldBe([a.OwnerId]);
            (await db.Users.AnyAsync(u => u.Id == b.OwnerId)).ShouldBeFalse();
        }

        using (tenantScopes.Begin(b.TenantId))
        {
            (await db.Users.AsNoTracking().Select(u => u.Id).ToListAsync()).ShouldBe([b.OwnerId]);
        }

        (await db.Users.CountAsync()).ShouldBe(0);
    }
}
