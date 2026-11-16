using System.Net;
using System.Net.Http.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Auth;
using NexDesk.Application.Common;
using NexDesk.Application.Users;
using NexDesk.Domain.Identity;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Users;

[Collection(ApiCollection.Name)]
public sealed class UsersAndInvitationsTests(ApiFactory factory)
{
    [Fact]
    public async Task Invitation_can_be_looked_up_and_accepted_once()
    {
        var workspace = await factory.CreateWorkspaceAsync("Invite Co");
        var email = TestWorkspaces.UniqueEmail("invitee");

        var created = await workspace.Client.PostAsJsonAsync(
            "/api/v1/invitations",
            new CreateInvitationRequest(email, Role.Agent));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var sent = factory.Emails.Last(email, "invitation");
        sent.Rendered.Subject.ShouldContain("Invite Co");
        sent.Rendered.Html.ShouldContain("/auth/accept-invite?token=");

        var anonymous = factory.CreateBareClient();
        var preview = await anonymous.GetAsync<InvitationPreview>(
            $"/api/v1/invitations/lookup?token={Uri.EscapeDataString(sent.Token)}");
        preview.ShouldBe(new InvitationPreview("Invite Co", email, Role.Agent));

        var accepted = await anonymous.PostAsJsonAsync(
            "/api/v1/invitations/accept",
            new AcceptInvitationRequest(sent.Token, "Ivy Invitee", TestWorkspaces.DefaultPassword));
        var auth = await accepted.ReadAsync<AuthResponse>();
        auth.Session.User.Role.ShouldBe("Agent");
        auth.Session.Tenant!.Id.ShouldBe(workspace.TenantId);
        auth.Session.Permissions.ShouldNotContain(Permissions.UsersManage);

        var again = await anonymous.PostAsJsonAsync(
            "/api/v1/invitations/accept",
            new AcceptInvitationRequest(sent.Token, "Someone Else", TestWorkspaces.DefaultPassword));
        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await again.ProblemCodeAsync()).ShouldBe("invalid-token");

        (await workspace.Client.GetAsync<List<InvitationDto>>("/api/v1/invitations")).ShouldBeEmpty();
        var users = await workspace.Client.GetAsync<PagedResult<UserDto>>("/api/v1/users");
        users.Items.Select(u => u.Email).ShouldBe([email, workspace.Email], ignoreOrder: true);
        users.Items.Single(u => u.Email == email).EmailVerified.ShouldBeTrue();
    }

    [Fact]
    public async Task Invitation_is_rejected_for_known_emails_and_duplicates()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var other = await factory.CreateWorkspaceAsync();
        var email = TestWorkspaces.UniqueEmail("dup");

        var existing = await workspace.Client.PostAsJsonAsync(
            "/api/v1/invitations",
            new CreateInvitationRequest(other.Email, Role.Agent));
        existing.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await existing.ProblemCodeAsync()).ShouldBe("email-taken");

        (await workspace.Client.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(email, Role.Agent)))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var duplicate = await workspace.Client.PostAsJsonAsync(
            "/api/v1/invitations",
            new CreateInvitationRequest(email.ToUpperInvariant(), Role.Viewer));
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await duplicate.ProblemCodeAsync()).ShouldBe("invitation-pending");
    }

    [Fact]
    public async Task Resend_replaces_the_link_and_revoke_disables_it()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var email = TestWorkspaces.UniqueEmail("resend");
        var invitation = await (await workspace.Client.PostAsJsonAsync(
            "/api/v1/invitations",
            new CreateInvitationRequest(email, Role.Admin))).ReadAsync<InvitationDto>();
        var firstToken = factory.Emails.Last(email, "invitation").Token;
        var anonymous = factory.CreateBareClient();

        (await workspace.Client.PostAsync($"/api/v1/invitations/{invitation.Id}/resend", null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondToken = factory.Emails.Last(email, "invitation").Token;
        secondToken.ShouldNotBe(firstToken);
        (await anonymous.GetAsync($"/api/v1/invitations/lookup?token={Uri.EscapeDataString(firstToken)}"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await anonymous.GetAsync($"/api/v1/invitations/lookup?token={Uri.EscapeDataString(secondToken)}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await workspace.Client.DeleteAsync($"/api/v1/invitations/{invitation.Id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await anonymous.GetAsync($"/api/v1/invitations/lookup?token={Uri.EscapeDataString(secondToken)}"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await workspace.Client.GetAsync<List<InvitationDto>>("/api/v1/invitations")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Agents_cannot_manage_users_and_admins_cannot_touch_owners()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var admin = await factory.AddMemberAsync(workspace, Role.Admin, "Ada Admin");

        (await agent.Client.PostAsJsonAsync(
            "/api/v1/invitations",
            new CreateInvitationRequest(TestWorkspaces.UniqueEmail(), Role.Agent)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.PutAsJsonAsync($"/api/v1/users/{admin.UserId}/role", new ChangeRoleRequest(Role.Viewer)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.GetAsync("/api/v1/users")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var inviteOwner = await admin.Client.PostAsJsonAsync(
            "/api/v1/invitations",
            new CreateInvitationRequest(TestWorkspaces.UniqueEmail(), Role.Owner));
        (await inviteOwner.ProblemCodeAsync()).ShouldBe("insufficient-role");
        var grantOwner = await admin.Client.PutAsJsonAsync(
            $"/api/v1/users/{agent.UserId}/role",
            new ChangeRoleRequest(Role.Owner));
        (await grantOwner.ProblemCodeAsync()).ShouldBe("insufficient-role");
        var demoteOwner = await admin.Client.PutAsJsonAsync(
            $"/api/v1/users/{workspace.OwnerId}/role",
            new ChangeRoleRequest(Role.Agent));
        (await demoteOwner.ProblemCodeAsync()).ShouldBe("insufficient-role");
        var deactivateOwner = await admin.Client.PostAsync($"/api/v1/users/{workspace.OwnerId}/deactivate", null);
        (await deactivateOwner.ProblemCodeAsync()).ShouldBe("insufficient-role");

        var self = await workspace.Client.PutAsJsonAsync(
            $"/api/v1/users/{workspace.OwnerId}/role",
            new ChangeRoleRequest(Role.Agent));
        self.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await self.ProblemCodeAsync()).ShouldBe("cannot-modify-self");
    }

    [Fact]
    public async Task Changing_a_role_invalidates_the_old_token_and_the_new_one_carries_the_new_permissions()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var member = await factory.AddMemberAsync(workspace, Role.Agent);

        var changed = await (await workspace.Client.PutAsJsonAsync(
            $"/api/v1/users/{member.UserId}/role",
            new ChangeRoleRequest(Role.Admin))).ReadAsync<UserDto>();
        changed.Role.ShouldBe(Role.Admin);

        (await member.Client.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var refreshed = await (await factory.CreateBareClient().RefreshAsync(member.RefreshCookie)).ReadAsync<AuthResponse>();
        refreshed.Session.User.Role.ShouldBe("Admin");
        refreshed.Session.Permissions.ShouldContain(Permissions.UsersManage);
    }

    [Fact]
    public async Task Deactivating_a_user_ends_their_sessions_until_they_are_reactivated()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var member = await factory.AddMemberAsync(workspace, Role.Agent);
        var anonymous = factory.CreateBareClient();

        var deactivated = await (await workspace.Client.PostAsync($"/api/v1/users/{member.UserId}/deactivate", null))
            .ReadAsync<UserDto>();
        deactivated.IsActive.ShouldBeFalse();

        (await member.Client.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.RefreshAsync(member.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(member.Email, member.Password));
        (await login.ProblemCodeAsync()).ShouldBe("account-disabled");

        (await workspace.Client.PostAsync($"/api/v1/users/{member.UserId}/reactivate", null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(member.Email, member.Password)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Users_can_be_searched_filtered_sorted_and_paged()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await factory.AddMemberAsync(workspace, Role.Agent, "Zed Zimmer");
        await factory.AddMemberAsync(workspace, Role.Viewer, "Amy Abbott");

        var byName = await workspace.Client.GetAsync<PagedResult<UserDto>>("/api/v1/users?sort=-name&pageSize=2");
        byName.TotalCount.ShouldBe(3);
        byName.Items.Select(u => u.FullName).ShouldBe(["Zed Zimmer", "Olivia Owner"]);

        var search = await workspace.Client.GetAsync<PagedResult<UserDto>>("/api/v1/users?search=ABB");
        search.Items.Select(u => u.FullName).ShouldBe(["Amy Abbott"]);

        var viewers = await workspace.Client.GetAsync<PagedResult<UserDto>>("/api/v1/users?role=Viewer");
        viewers.Items.ShouldHaveSingleItem().Role.ShouldBe(Role.Viewer);

        (await workspace.Client.GetAsync("/api/v1/users?pageSize=500")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_user_can_update_their_profile_availability_and_password()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var member = await factory.AddMemberAsync(workspace, Role.Agent);
        const string newPassword = "Even-better-pass-7";

        var session = await (await member.Client.PutAsJsonAsync(
            "/api/v1/users/me/profile",
            new UpdateProfileRequest("Aaron A. Agent", "ar"))).ReadAsync<SessionDto>();
        session.User.FullName.ShouldBe("Aaron A. Agent");
        session.User.Language.ShouldBe("ar");

        var available = await (await member.Client.PutAsJsonAsync(
            "/api/v1/users/me/availability",
            new SetAvailabilityRequest(Availability.Online))).ReadAsync<UserDto>();
        available.Availability.ShouldBe(Availability.Online);

        var wrong = await member.Client.PostAsJsonAsync(
            "/api/v1/users/me/change-password",
            new ChangePasswordRequest("not-my-password-1", newPassword));
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await wrong.ProblemCodeAsync()).ShouldBe("wrong-password");

        var changed = await member.Client.PostAsJsonAsync(
            "/api/v1/users/me/change-password",
            new ChangePasswordRequest(member.Password, newPassword));
        var auth = await changed.ReadAsync<AuthResponse>();
        auth.AccessToken.ShouldNotBeNullOrEmpty();

        var anonymous = factory.CreateBareClient();
        (await anonymous.RefreshAsync(member.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.RefreshAsync(changed.ReadRefreshCookie()!)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(member.Email, member.Password)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(member.Email, newPassword)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
