using System.Net;
using System.Net.Http.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Common;
using NexDesk.Application.Sla;
using NexDesk.Application.Teams;
using NexDesk.Application.Users;
using NexDesk.Domain.Identity;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Tenancy;

/// <summary>Tenant B must never read or change tenant A's users, invitations, teams or settings.</summary>
[Collection(ApiCollection.Name)]
public sealed class WorkspaceIsolationTests(ApiFactory factory)
{
    [Fact]
    public async Task Users_of_another_tenant_are_invisible_and_untouchable()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var memberOfA = await factory.AddMemberAsync(a, Role.Agent);

        var listedInB = await b.Client.GetAsync<PagedResult<UserDto>>("/api/v1/users");
        listedInB.Items.Select(u => u.Id).ShouldBe([b.OwnerId]);

        (await b.Client.GetAsync($"/api/v1/users/{memberOfA.UserId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PutAsJsonAsync($"/api/v1/users/{memberOfA.UserId}/role", new ChangeRoleRequest(Role.Admin)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PostAsync($"/api/v1/users/{memberOfA.UserId}/deactivate", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PostAsync($"/api/v1/users/{memberOfA.UserId}/reactivate", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var unchanged = await a.Client.GetAsync<UserDto>($"/api/v1/users/{memberOfA.UserId}");
        unchanged.Role.ShouldBe(Role.Agent);
        unchanged.IsActive.ShouldBeTrue();
        (await memberOfA.Client.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Invitations_of_another_tenant_are_invisible_and_untouchable()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var email = TestWorkspaces.UniqueEmail("pending");
        var invitation = await (await a.Client.PostAsJsonAsync(
            "/api/v1/invitations",
            new CreateInvitationRequest(email, Role.Agent))).ReadAsync<InvitationDto>();

        (await b.Client.GetAsync<List<InvitationDto>>("/api/v1/invitations")).ShouldBeEmpty();
        (await b.Client.DeleteAsync($"/api/v1/invitations/{invitation.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PostAsync($"/api/v1/invitations/{invitation.Id}/resend", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        factory.Emails.Count(email, "invitation").ShouldBe(1);
        (await a.Client.GetAsync<List<InvitationDto>>("/api/v1/invitations"))
            .ShouldHaveSingleItem().Status.ShouldBe(InvitationStatus.Pending);
    }

    [Fact]
    public async Task Teams_of_another_tenant_are_invisible_and_untouchable()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var team = await (await a.Client.PostAsJsonAsync(
            "/api/v1/teams",
            new SaveTeamRequest("Tier 1", null, [a.OwnerId]))).ReadAsync<TeamDto>();

        (await b.Client.GetAsync<List<TeamDto>>("/api/v1/teams")).ShouldBeEmpty();
        (await b.Client.PutAsJsonAsync($"/api/v1/teams/{team.Id}", new SaveTeamRequest("Hijacked", null, null)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PutAsJsonAsync($"/api/v1/teams/{team.Id}/members", new SetTeamMembersRequest([b.OwnerId])))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.DeleteAsync($"/api/v1/teams/{team.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var foreignMember = await b.Client.PostAsJsonAsync(
            "/api/v1/teams",
            new SaveTeamRequest("Mixed", null, [a.OwnerId]));
        foreignMember.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await foreignMember.ProblemCodeAsync()).ShouldBe("unknown-member");

        // The same team name is free in every tenant.
        (await b.Client.PostAsJsonAsync("/api/v1/teams", new SaveTeamRequest("Tier 1", null, null)))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var unchanged = (await a.Client.GetAsync<List<TeamDto>>("/api/v1/teams")).ShouldHaveSingleItem();
        unchanged.Name.ShouldBe("Tier 1");
        unchanged.Members.Select(m => m.UserId).ShouldBe([a.OwnerId]);
    }

    [Fact]
    public async Task Business_hours_and_onboarding_are_kept_per_tenant()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var hours = await b.Client.GetAsync<BusinessHoursDto>("/api/v1/sla/business-hours");

        (await b.Client.PutAsJsonAsync(
            "/api/v1/sla/business-hours",
            new UpdateBusinessHoursRequest("Asia/Tokyo", hours.Days.Select(d => d with { IsWorkingDay = true }).ToList()),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await b.Client.PostAsync("/api/v1/onboarding/complete", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var hoursOfA = await a.Client.GetAsync<BusinessHoursDto>("/api/v1/sla/business-hours");
        hoursOfA.Timezone.ShouldBe("UTC");
        hoursOfA.Days.Count(d => d.IsWorkingDay).ShouldBe(5);
        (await a.Client.GetAsync<NexDesk.Application.Onboarding.OnboardingStatusDto>("/api/v1/onboarding"))
            .Completed.ShouldBeFalse();
    }
}
