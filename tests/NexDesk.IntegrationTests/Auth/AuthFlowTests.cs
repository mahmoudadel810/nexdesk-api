using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Domain.Identity;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Auth;

[Collection(ApiCollection.Name)]
public sealed class AuthFlowTests(ApiFactory factory)
{
    [Fact]
    public async Task Register_sends_a_verification_email_and_blocks_login_until_verified()
    {
        var client = factory.CreateBareClient();
        var registration = TestWorkspaces.NewRegistration(workspaceName: "Globex Support");

        var registered = await client.PostAsJsonAsync("/api/v1/auth/register", registration);

        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await registered.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("slug").GetString()!.ShouldStartWith("globex-support");
        var email = factory.Emails.Last(registration.Email, "verify-email");
        email.Rendered.Html.ShouldContain("/auth/verify-email?token=");
        email.Rendered.Subject.ShouldBe("Confirm your email address");

        var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(registration.Email, registration.Password));
        login.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await login.ProblemCodeAsync()).ShouldBe("email-not-verified");
    }

    [Fact]
    public async Task Verify_email_signs_the_owner_in_and_the_token_works_only_once()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        var me = await workspace.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        me.GetProperty("user").GetProperty("email").GetString().ShouldBe(workspace.Email);
        me.GetProperty("user").GetProperty("role").GetString().ShouldBe("Owner");
        me.GetProperty("tenant").GetProperty("id").GetGuid().ShouldBe(workspace.TenantId);
        me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString())
            .ShouldBe(Permissions.For(Role.Owner).Order(StringComparer.Ordinal), ignoreOrder: true);

        var token = factory.Emails.Last(workspace.Email, "verify-email").Token;
        var again = await factory.CreateBareClient()
            .PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(token));
        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await again.ProblemCodeAsync()).ShouldBe("invalid-token");
    }

    [Fact]
    public async Task Register_rejects_a_duplicate_email_and_a_taken_slug()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var client = factory.CreateBareClient();

        var sameEmail = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            TestWorkspaces.NewRegistration(email: workspace.Email.ToUpperInvariant()));
        sameEmail.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await sameEmail.ProblemCodeAsync()).ShouldBe("email-taken");

        var sameSlug = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            TestWorkspaces.NewRegistration(slug: workspace.Slug));
        sameSlug.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await sameSlug.ProblemCodeAsync()).ShouldBe("slug-taken");
    }

    [Fact]
    public async Task Register_derives_a_free_slug_when_the_workspace_name_is_already_used()
    {
        var name = $"Initech {Guid.NewGuid():N}"[..18];
        var first = await factory.CreateWorkspaceAsync(name);
        var second = await factory.CreateWorkspaceAsync(name);

        second.Slug.ShouldBe($"{first.Slug}-2");
    }

    [Fact]
    public async Task Register_reports_validation_errors_per_field_in_camel_case()
    {
        var response = await factory.CreateBareClient().PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest("", "Bad Slug!", "", "not-an-email", "short", "xx", "Mars/Olympus"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().ShouldBe("validation");
        var fields = problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToArray();
        fields.ShouldBe(
            ["workspaceName", "slug", "fullName", "email", "password", "language", "timezone"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Login_returns_tokens_and_rejects_a_wrong_password()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var client = factory.CreateBareClient();

        var wrong = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(workspace.Email, "Wrong-password-1"));
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await wrong.ProblemCodeAsync()).ShouldBe("invalid-credentials");

        var unknown = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(TestWorkspaces.UniqueEmail("nobody"), workspace.Password));
        (await unknown.ProblemCodeAsync()).ShouldBe("invalid-credentials");

        var ok = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(workspace.Email, workspace.Password));
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var auth = (await ok.Content.ReadFromJsonAsync<AuthResponse>())!;
        auth.AccessToken.ShouldNotBeNullOrEmpty();
        auth.Session.Tenant!.Id.ShouldBe(workspace.TenantId);

        var cookie = ok.Headers.GetValues("Set-Cookie").Single();
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain("path=/api/v1/auth", Case.Insensitive);
    }

    [Fact]
    public async Task Login_locks_the_account_after_repeated_failures()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var client = factory.CreateBareClient();

        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < User.MaxFailedLogins; attempt++)
        {
            last = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new LoginRequest(workspace.Email, "Wrong-password-1"));
        }

        (await last!.ProblemCodeAsync()).ShouldBe("account-locked");
        var correct = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(workspace.Email, workspace.Password));
        correct.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await correct.ProblemCodeAsync()).ShouldBe("account-locked");
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reuse_revokes_the_whole_family()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var client = factory.CreateBareClient();

        var rotated = await client.RefreshAsync(workspace.RefreshCookie);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var newCookie = rotated.ReadRefreshCookie()!;
        newCookie.ShouldNotBe(workspace.RefreshCookie);
        var auth = (await rotated.Content.ReadFromJsonAsync<AuthResponse>())!;
        auth.Session.User.Id.ShouldBe(workspace.OwnerId);

        var replayed = await client.RefreshAsync(workspace.RefreshCookie);
        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await replayed.ProblemCodeAsync()).ShouldBe("invalid-refresh-token");

        var afterReuse = await client.RefreshAsync(newCookie);
        afterReuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_unauthorized()
    {
        var response = await factory.CreateBareClient().PostAsync("/api/v1/auth/refresh", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ProblemCodeAsync()).ShouldBe("invalid-refresh-token");
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var client = factory.CreateBareClient();

        var logout = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout").WithRefreshCookie(workspace.RefreshCookie));
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.RefreshAsync(workspace.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forgot_password_answers_202_without_revealing_whether_the_account_exists()
    {
        var unknown = TestWorkspaces.UniqueEmail("ghost");

        var response = await factory.CreateBareClient()
            .PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest(unknown));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Emails.Count(unknown, "reset-password").ShouldBe(0);
    }

    [Fact]
    public async Task Reset_password_changes_the_password_and_ends_existing_sessions()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var client = factory.CreateBareClient();
        const string newPassword = "Brand-new-pass-9";

        (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest(workspace.Email)))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = factory.Emails.Last(workspace.Email, "reset-password").Token;

        var reset = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new ResetPasswordRequest(token, newPassword));
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await workspace.Client.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.RefreshAsync(workspace.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(workspace.Email, workspace.Password)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(workspace.Email, newPassword)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var reused = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new ResetPasswordRequest(token, "Another-pass-10"));
        (await reused.ProblemCodeAsync()).ShouldBe("invalid-token");
    }

    [Fact]
    public async Task Resend_verification_issues_a_new_link_only_for_unverified_accounts()
    {
        var client = factory.CreateBareClient();
        var registration = TestWorkspaces.NewRegistration();
        await client.PostAsJsonAsync("/api/v1/auth/register", registration);

        (await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new EmailRequest(registration.Email)))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Emails.Count(registration.Email, "verify-email").ShouldBe(2);

        var token = factory.Emails.Last(registration.Email, "verify-email").Token;
        (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(token)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new EmailRequest(registration.Email));
        factory.Emails.Count(registration.Email, "verify-email").ShouldBe(2);
    }

    [Fact]
    public async Task Me_requires_a_valid_access_token()
    {
        var client = factory.CreateBareClient();

        var anonymous = await client.GetAsync("/api/v1/auth/me");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        (await client.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
