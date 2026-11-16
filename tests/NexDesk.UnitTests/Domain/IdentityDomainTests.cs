using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;

namespace NexDesk.UnitTests.Domain;

public sealed class IdentityDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("acme", true)]
    [InlineData("acme-support-2", true)]
    [InlineData("ab", false)]
    [InlineData("Acme", false)]
    [InlineData("acme--support", false)]
    [InlineData("-acme", false)]
    [InlineData("acme_support", false)]
    [InlineData("admin", false)]
    [InlineData("portal", false)]
    public void Slug_validation_accepts_only_lowercase_hyphenated_names(string slug, bool expected) =>
        Tenant.IsValidSlug(slug).ShouldBe(expected);

    [Theory]
    [InlineData("Acme Inc.", "acme-inc")]
    [InlineData("  Globex   Support  ", "globex-support")]
    [InlineData("شركة", "")]
    [InlineData("Admin", "")]
    public void Slug_suggestion_is_derived_from_the_workspace_name(string name, string expected) =>
        Tenant.SuggestSlug(name).ShouldBe(expected);

    [Fact]
    public void Each_role_includes_every_permission_of_the_role_below_it()
    {
        Permissions.For(Role.Agent).IsSupersetOf(Permissions.For(Role.Viewer)).ShouldBeTrue();
        Permissions.For(Role.Admin).IsSupersetOf(Permissions.For(Role.Agent)).ShouldBeTrue();
        Permissions.For(Role.Owner).IsSupersetOf(Permissions.For(Role.Admin)).ShouldBeTrue();
    }

    [Fact]
    public void Only_the_owner_manages_billing_and_viewers_cannot_change_anything()
    {
        Permissions.For(Role.Owner).ShouldContain(Permissions.BillingManage);
        Permissions.For(Role.Admin).ShouldNotContain(Permissions.BillingManage);
        Permissions.For(Role.Agent).ShouldNotContain(Permissions.UsersManage);
        Permissions.For(Role.Viewer).ShouldAllBe(permission => permission.EndsWith(".view"));
    }

    [Fact]
    public void User_is_locked_after_the_failure_limit_and_unlocked_when_the_lockout_expires()
    {
        var user = User.CreateStaff(Guid.NewGuid(), " Owner@Example.Test ", "Olivia", Role.Owner, "en");
        user.Email.ShouldBe("owner@example.test");

        for (var attempt = 1; attempt < User.MaxFailedLogins; attempt++)
        {
            user.RecordFailedLogin(Now);
            user.IsLockedOut(Now).ShouldBeFalse();
        }

        user.RecordFailedLogin(Now);

        user.IsLockedOut(Now).ShouldBeTrue();
        user.IsLockedOut(Now + User.LockoutDuration).ShouldBeFalse();
    }

    [Fact]
    public void Changing_the_password_bumps_the_security_version_and_clears_the_lockout()
    {
        var user = User.CreateStaff(Guid.NewGuid(), "a@example.test", "A", Role.Agent, "en");
        for (var attempt = 0; attempt < User.MaxFailedLogins; attempt++)
        {
            user.RecordFailedLogin(Now);
        }

        var before = user.SecurityVersion;
        user.ChangePassword("new-hash");

        user.SecurityVersion.ShouldBe(before + 1);
        user.IsLockedOut(Now).ShouldBeFalse();
    }

    [Fact]
    public void Refresh_token_is_unusable_once_revoked_or_expired()
    {
        var token = RefreshToken.Issue(SubjectType.Staff, Guid.NewGuid(), "hash", Now, TimeSpan.FromDays(1), null);
        var successor = RefreshToken.Issue(
            SubjectType.Staff, token.SubjectId, "hash-2", Now, TimeSpan.FromDays(1), null, token.FamilyId);

        token.IsUsable(Now).ShouldBeTrue();
        token.IsUsable(Now.AddDays(2)).ShouldBeFalse();

        token.ReplaceWith(successor, Now);

        token.IsUsable(Now).ShouldBeFalse();
        token.ReplacedById.ShouldBe(successor.Id);
        successor.FamilyId.ShouldBe(token.FamilyId);
    }

    [Fact]
    public void One_time_token_cannot_be_used_twice()
    {
        var token = OneTimeToken.Issue(
            SubjectType.Staff, Guid.NewGuid(), OneTimeTokenPurpose.PasswordReset, "hash", Now, TimeSpan.FromHours(1));

        token.IsUsable(Now).ShouldBeTrue();
        token.Consume(Now);

        token.IsUsable(Now).ShouldBeFalse();
    }
}
