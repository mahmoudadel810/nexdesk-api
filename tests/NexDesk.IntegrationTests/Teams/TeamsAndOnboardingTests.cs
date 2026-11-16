using System.Net;
using System.Net.Http.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Auth;
using NexDesk.Application.Onboarding;
using NexDesk.Application.Sla;
using NexDesk.Application.Teams;
using NexDesk.Domain.Identity;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Teams;

[Collection(ApiCollection.Name)]
public sealed class TeamsAndOnboardingTests(ApiFactory factory)
{
    [Fact]
    public async Task Teams_can_be_created_renamed_staffed_and_deleted()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);

        var created = await workspace.Client.PostAsJsonAsync(
            "/api/v1/teams",
            new SaveTeamRequest("  Billing  ", "Invoices and refunds", [workspace.OwnerId]));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var team = await created.ReadAsync<TeamDto>();
        team.Name.ShouldBe("Billing");
        team.Members.Select(m => m.UserId).ShouldBe([workspace.OwnerId]);

        var duplicate = await workspace.Client.PostAsJsonAsync("/api/v1/teams", new SaveTeamRequest("billing", null, null));
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await duplicate.ProblemCodeAsync()).ShouldBe("team-name-taken");

        var renamed = await (await workspace.Client.PutAsJsonAsync(
            $"/api/v1/teams/{team.Id}",
            new SaveTeamRequest("Billing & Payments", null, null))).ReadAsync<TeamDto>();
        renamed.Name.ShouldBe("Billing & Payments");
        renamed.Description.ShouldBeNull();

        var staffed = await (await workspace.Client.PutAsJsonAsync(
            $"/api/v1/teams/{team.Id}/members",
            new SetTeamMembersRequest([agent.UserId]))).ReadAsync<TeamDto>();
        staffed.Members.Select(m => m.UserId).ShouldBe([agent.UserId]);

        (await agent.Client.GetAsync<List<TeamDto>>("/api/v1/teams")).ShouldHaveSingleItem().Id.ShouldBe(team.Id);
        (await agent.Client.DeleteAsync($"/api/v1/teams/{team.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await workspace.Client.DeleteAsync($"/api/v1/teams/{team.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workspace.Client.GetAsync<List<TeamDto>>("/api/v1/teams")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Team_validation_rejects_empty_names()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        var response = await workspace.Client.PostAsJsonAsync("/api/v1/teams", new SaveTeamRequest(" ", null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ProblemCodeAsync()).ShouldBe("validation");
    }

    [Fact]
    public async Task New_workspaces_start_with_weekday_business_hours_that_can_be_changed()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        var initial = await workspace.Client.GetAsync<BusinessHoursDto>("/api/v1/sla/business-hours");
        initial.Timezone.ShouldBe("UTC");
        initial.Days.Count.ShouldBe(7);
        initial.Days.Where(d => d.IsWorkingDay).Select(d => d.Day).ShouldBe(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
        initial.Days[1].StartTime.ShouldBe(new TimeOnly(9, 0));

        var days = initial.Days
            .Select(d => d.Day is DayOfWeek.Friday or DayOfWeek.Saturday
                ? d with { IsWorkingDay = false }
                : d with { IsWorkingDay = true, StartTime = new TimeOnly(8, 30), EndTime = new TimeOnly(16, 30) })
            .ToList();
        var updated = await (await workspace.Client.PutAsJsonAsync(
            "/api/v1/sla/business-hours",
            new UpdateBusinessHoursRequest("Africa/Cairo", days),
            TestJson.Options)).ReadAsync<BusinessHoursDto>();

        updated.Timezone.ShouldBe("Africa/Cairo");
        updated.Days.Single(d => d.Day == DayOfWeek.Sunday).ShouldBe(
            new BusinessDayDto(DayOfWeek.Sunday, true, new TimeOnly(8, 30), new TimeOnly(16, 30)));
        updated.Days.Single(d => d.Day == DayOfWeek.Friday).IsWorkingDay.ShouldBeFalse();
        (await workspace.Client.GetAsync<SessionDto>("/api/v1/auth/me")).Tenant!.Timezone.ShouldBe("Africa/Cairo");
    }

    [Fact]
    public async Task Business_hours_validation_rejects_bad_schedules()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var current = await workspace.Client.GetAsync<BusinessHoursDto>("/api/v1/sla/business-hours");

        var inverted = current.Days
            .Select(d => d with { IsWorkingDay = true, StartTime = new TimeOnly(18, 0), EndTime = new TimeOnly(9, 0) })
            .ToList();
        var allClosed = current.Days.Select(d => d with { IsWorkingDay = false }).ToList();

        foreach (var (timezone, days) in new[]
        {
            ("UTC", inverted),
            ("UTC", allClosed),
            ("UTC", current.Days.Take(6).ToList()),
            ("Mars/Olympus", current.Days.ToList()),
        })
        {
            var response = await workspace.Client.PutAsJsonAsync(
                "/api/v1/sla/business-hours",
                new UpdateBusinessHoursRequest(timezone, days),
                TestJson.Options);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        (await agent.Client.PutAsJsonAsync(
            "/api/v1/sla/business-hours",
            new UpdateBusinessHoursRequest("UTC", current.Days),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Onboarding_tracks_progress_and_is_completed_once()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        var start = await workspace.Client.GetAsync<OnboardingStatusDto>("/api/v1/onboarding");
        start.ShouldBe(new OnboardingStatusDto(false, 0, 1, 0));
        (await workspace.Client.GetAsync<SessionDto>("/api/v1/auth/me")).Tenant!.OnboardingCompleted.ShouldBeFalse();

        await workspace.Client.PostAsJsonAsync("/api/v1/teams", new SaveTeamRequest("Support", null, null));
        await workspace.Client.PostAsJsonAsync(
            "/api/v1/invitations",
            new CreateInvitationRequest(TestWorkspaces.UniqueEmail(), Role.Agent));

        var done = await (await workspace.Client.PostAsync("/api/v1/onboarding/complete", null))
            .ReadAsync<OnboardingStatusDto>();
        done.ShouldBe(new OnboardingStatusDto(true, 1, 1, 1));
        (await workspace.Client.GetAsync<SessionDto>("/api/v1/auth/me")).Tenant!.OnboardingCompleted.ShouldBeTrue();

        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        (await agent.Client.PostAsync("/api/v1/onboarding/complete", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
