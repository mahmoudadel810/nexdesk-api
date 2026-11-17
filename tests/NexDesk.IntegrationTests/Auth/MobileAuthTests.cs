using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Auth;
using NexDesk.Domain.Identity;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Auth;

[Collection(ApiCollection.Name)]
public sealed class MobileAuthTests(ApiFactory factory)
{
    private const string Login = "/api/v1/auth/mobile/login";
    private const string Refresh = "/api/v1/auth/mobile/refresh";
    private const string Logout = "/api/v1/auth/mobile/logout";

    private async Task<MobileAuthResponse> SignInAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync(Login, new MobileLoginRequest(email, password, "Pixel 8"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadAsync<MobileAuthResponse>();
    }

    [Fact]
    public async Task Login_returns_both_tokens_without_a_cookie_and_the_access_token_names_its_session()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var client = factory.CreateBareClient();

        var response = await client.PostAsJsonAsync(Login, new MobileLoginRequest(workspace.Email, workspace.Password, "Pixel 8"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        var auth = await response.ReadAsync<MobileAuthResponse>();
        auth.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        auth.RefreshTokenExpiresAt.ShouldBeGreaterThan(auth.ExpiresAt);
        auth.Session.User.Id.ShouldBe(workspace.OwnerId);
        Guid.TryParse(new JsonWebToken(auth.AccessToken).GetClaim("sid").Value, out _).ShouldBeTrue();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        (await client.GetAsync<SessionDto>("/api/v1/auth/me")).User.Email.ShouldBe(workspace.Email);

        var wrong = await factory.CreateBareClient().PostAsJsonAsync(Login, new MobileLoginRequest(workspace.Email, "Wrong-pass-1", null));
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await wrong.ProblemCodeAsync()).ShouldBe("invalid-credentials");
    }

    [Fact]
    public async Task Refresh_rotates_and_a_replayed_token_ends_the_whole_session()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var client = factory.CreateBareClient();
        var first = await SignInAsync(client, workspace.Email, workspace.Password);

        var rotated = await (await client.PostAsJsonAsync(Refresh, new MobileRefreshRequest(first.RefreshToken)))
            .ReadAsync<MobileAuthResponse>();
        rotated.RefreshToken.ShouldNotBe(first.RefreshToken);
        new JsonWebToken(rotated.AccessToken).GetClaim("sid").Value
            .ShouldBe(new JsonWebToken(first.AccessToken).GetClaim("sid").Value);

        var replay = await client.PostAsJsonAsync(Refresh, new MobileRefreshRequest(first.RefreshToken));
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await replay.ProblemCodeAsync()).ShouldBe("invalid-refresh-token");
        (await client.PostAsJsonAsync(Refresh, new MobileRefreshRequest(rotated.RefreshToken)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Two_refreshes_at_once_let_one_through_and_then_end_the_session()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var client = factory.CreateBareClient();
        var auth = await SignInAsync(client, workspace.Email, workspace.Password);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(Refresh, new MobileRefreshRequest(auth.RefreshToken)),
            client.PostAsJsonAsync(Refresh, new MobileRefreshRequest(auth.RefreshToken)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).ShouldBe(1);
        var winner = await responses.Single(r => r.IsSuccessStatusCode).ReadAsync<MobileAuthResponse>();
        (await client.PostAsJsonAsync(Refresh, new MobileRefreshRequest(winner.RefreshToken)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_is_harmless_to_repeat()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var client = factory.CreateBareClient();
        var auth = await SignInAsync(client, workspace.Email, workspace.Password);

        (await client.PostAsJsonAsync(Logout, new MobileRefreshRequest(auth.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Logout, new MobileRefreshRequest(auth.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Logout, new MobileRefreshRequest(null))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Refresh, new MobileRefreshRequest(auth.RefreshToken)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cookies_and_body_tokens_never_cross_over()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var client = factory.CreateBareClient();

        // The browser's cookie reaches /auth/mobile too (its path is /api/v1/auth), but is ignored there.
        var cookieOnly = new HttpRequestMessage(HttpMethod.Post, Refresh) { Content = JsonContent.Create(new MobileRefreshRequest(null)) }
            .WithRefreshCookie(workspace.RefreshCookie);
        var refused = await client.SendAsync(cookieOnly);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        refused.Headers.Contains("Set-Cookie").ShouldBeFalse();
        (await client.RefreshAsync(workspace.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // And the cookie endpoint takes nothing from the body.
        var mobile = await SignInAsync(client, workspace.Email, workspace.Password);
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new MobileRefreshRequest(mobile.RefreshToken)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync(Refresh, new MobileRefreshRequest(mobile.RefreshToken)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_deactivated_member_or_a_suspended_workspace_cannot_refresh()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var member = await factory.AddMemberAsync(workspace, Role.Agent);
        var client = factory.CreateBareClient();
        var memberAuth = await SignInAsync(client, member.Email, member.Password);
        var ownerAuth = await SignInAsync(client, workspace.Email, workspace.Password);

        (await workspace.Client.PostAsync($"/api/v1/users/{member.UserId}/deactivate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var deactivated = await client.PostAsJsonAsync(Refresh, new MobileRefreshRequest(memberAuth.RefreshToken));
        deactivated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var admin = factory.CreateBareClient();
        var adminAuth = await (await admin.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest(ApiFactory.PlatformAdminEmail, ApiFactory.PlatformAdminPassword))).ReadAsync<AuthResponse>();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminAuth.AccessToken);
        (await admin.PostAsJsonAsync($"/api/v1/admin/tenants/{workspace.TenantId}/suspend", new SuspendTenantRequest()))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var suspended = await client.PostAsJsonAsync(Refresh, new MobileRefreshRequest(ownerAuth.RefreshToken));
        suspended.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await suspended.ProblemCodeAsync()).ShouldBe("tenant-suspended");
    }
}
