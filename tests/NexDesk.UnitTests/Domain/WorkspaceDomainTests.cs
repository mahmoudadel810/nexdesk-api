using NexDesk.Domain.Identity;
using NexDesk.Domain.Sla;
using NexDesk.Domain.Teams;

namespace NexDesk.UnitTests.Domain;

public sealed class WorkspaceDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Invitation_status_follows_its_lifecycle()
    {
        var invitation = Invitation.Create(" New.Agent@Example.Test ", Role.Agent, "hash", Guid.NewGuid(), Now);

        invitation.Email.ShouldBe("new.agent@example.test");
        invitation.StatusAt(Now).ShouldBe(InvitationStatus.Pending);
        invitation.StatusAt(Now + Invitation.Lifetime).ShouldBe(InvitationStatus.Expired);

        invitation.Renew("hash-2", Now + Invitation.Lifetime);
        invitation.StatusAt(Now + Invitation.Lifetime).ShouldBe(InvitationStatus.Pending);
        invitation.TokenHash.ShouldBe("hash-2");

        invitation.Revoke(Now);
        invitation.StatusAt(Now).ShouldBe(InvitationStatus.Revoked);

        invitation.Accept(Now);
        invitation.StatusAt(Now).ShouldBe(InvitationStatus.Accepted);
    }

    [Fact]
    public void Setting_team_members_adds_new_ones_and_drops_the_rest()
    {
        var (first, second, third) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var team = Team.Create("  Support ", "  ");
        team.Name.ShouldBe("Support");
        team.Description.ShouldBeNull();

        team.SetMembers([first, second]);
        team.SetMembers([second, third]);

        team.Members.Select(m => m.UserId).ShouldBe([second, third], ignoreOrder: true);
        team.Members.ShouldAllBe(m => m.TeamId == team.Id);
    }

    [Fact]
    public void Changing_a_role_bumps_the_security_version_only_when_the_role_differs()
    {
        var user = User.CreateStaff(Guid.NewGuid(), "a@example.test", "A", Role.Agent, "en");
        var version = user.SecurityVersion;

        user.ChangeRole(Role.Agent);
        user.SecurityVersion.ShouldBe(version);

        user.ChangeRole(Role.Admin);
        user.SecurityVersion.ShouldBe(version + 1);
    }

    [Fact]
    public void Deactivating_a_user_takes_them_offline_and_invalidates_tokens()
    {
        var user = User.CreateStaff(Guid.NewGuid(), "a@example.test", "A", Role.Agent, "en");
        user.SetAvailability(Availability.Online);
        var version = user.SecurityVersion;

        user.Deactivate();

        user.IsActive.ShouldBeFalse();
        user.Availability.ShouldBe(Availability.Offline);
        user.SecurityVersion.ShouldBe(version + 1);
    }

    [Fact]
    public void The_default_week_is_monday_to_friday_nine_to_five()
    {
        var week = BusinessHoursDay.DefaultWeek();

        week.Count.ShouldBe(7);
        week.Where(d => d.IsWorkingDay).Select(d => d.Day).ShouldBe(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            ignoreOrder: true);
        week.ShouldAllBe(d => d.StartTime == new TimeOnly(9, 0) && d.EndTime == new TimeOnly(17, 0));
    }

    [Fact]
    public void A_working_day_must_end_after_it_starts()
    {
        var day = BusinessHoursDay.DefaultWeek()[1];

        Should.Throw<ArgumentException>(() => day.Set(true, new TimeOnly(17, 0), new TimeOnly(9, 0)));
        Should.NotThrow(() => day.Set(false, new TimeOnly(17, 0), new TimeOnly(9, 0)));
    }
}
