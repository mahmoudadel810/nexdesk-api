using NexDesk.Api.RateLimiting;
using NexDesk.Application.Auth;
using NexDesk.Application.Common;

namespace NexDesk.Api.Endpoints;

public sealed record RegisterRequest(
    string WorkspaceName,
    string? Slug,
    string FullName,
    string Email,
    string Password,
    string? Language,
    string? Timezone);

public sealed record LoginRequest(string Email, string Password);

public sealed record EmailRequest(string Email);

public sealed record VerifyEmailRequest(string Token);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

/// <summary>The access token for API calls. The refresh token travels only in an HttpOnly cookie.</summary>
public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, SessionDto Session);

public sealed record MobileLoginRequest(string Email, string Password, string? DeviceName);

public sealed record MobileRefreshRequest(string? RefreshToken);

/// <summary>
/// Both tokens, for the mobile app, which keeps the refresh token in the device's secure storage. It rotates on
/// every refresh: store the new one before using the new access token.
/// </summary>
public sealed record MobileAuthResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    SessionDto Session);

public static class AuthEndpoints
{
    public const string RefreshCookie = "nexdesk_rt";
    private const string RefreshCookiePath = "/api/v1/auth";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        // Endpoints that take a password or send an email get a small budget per address. Refreshing a
        // session happens on every page load and stays under the general limit.
        var guarded = auth.MapGroup(string.Empty).RequireRateLimiting(RateLimitingSetup.AuthPolicy);

        guarded.MapPost("/register", async (
            RegisterRequest request,
            ICommandHandler<RegisterWorkspaceCommand, RegisterWorkspaceResult> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new RegisterWorkspaceCommand(
                    request.WorkspaceName,
                    request.Slug,
                    request.FullName,
                    request.Email,
                    request.Password,
                    request.Language,
                    request.Timezone),
                cancellationToken);
            return result.ToHttp(value => TypedResults.Created((string?)null, value));
        })
            .WithName("RegisterWorkspace")
            .WithSummary("Creates a workspace and its owner account, then emails a verification link.")
            .Produces<RegisterWorkspaceResult>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        guarded.MapPost("/login", async (
            LoginRequest request,
            HttpContext http,
            ICommandHandler<LoginCommand, AuthTokens> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new LoginCommand(request.Email, request.Password, UserAgent(http)),
                cancellationToken);
            return SignIn(http, result);
        })
            .WithName("Login")
            .WithSummary("Signs a staff user in and sets the refresh-token cookie.")
            .Produces<AuthResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        auth.MapPost("/refresh", async (
            HttpContext http,
            ICommandHandler<RefreshSessionCommand, AuthTokens> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(
                new RefreshSessionCommand(http.Request.Cookies[RefreshCookie], UserAgent(http)),
                cancellationToken);
            if (!result.IsSuccess)
            {
                ClearRefreshCookie(http);
            }

            return SignIn(http, result);
        })
            .WithName("RefreshSession")
            .WithSummary("Rotates the refresh token from the cookie and returns a new access token.")
            .Produces<AuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/logout", async (
            HttpContext http,
            ICommandHandler<LogoutCommand, Success> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new LogoutCommand(http.Request.Cookies[RefreshCookie]), cancellationToken);
            ClearRefreshCookie(http);
            return result.ToNoContent();
        })
            .WithName("Logout")
            .WithSummary("Revokes the current refresh-token family and clears the cookie.")
            .Produces(StatusCodes.Status204NoContent);

        guarded.MapPost("/verify-email", async (
            VerifyEmailRequest request,
            HttpContext http,
            ICommandHandler<VerifyEmailCommand, AuthTokens> handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.Handle(new VerifyEmailCommand(request.Token, UserAgent(http)), cancellationToken);
            return SignIn(http, result);
        })
            .WithName("VerifyEmail")
            .WithSummary("Confirms an email address with the emailed token and signs the user in.")
            .Produces<AuthResponse>()
            .ProducesValidationProblem();

        guarded.MapPost("/resend-verification", async (
            EmailRequest request,
            ICommandHandler<ResendVerificationCommand, Success> handler,
            CancellationToken cancellationToken) =>
            (await handler.Handle(new ResendVerificationCommand(request.Email), cancellationToken))
            .ToHttp(_ => TypedResults.Accepted((string?)null)))
            .WithName("ResendVerification")
            .WithSummary("Sends a new verification link if the account exists and is not yet verified.")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();

        guarded.MapPost("/forgot-password", async (
            EmailRequest request,
            ICommandHandler<ForgotPasswordCommand, Success> handler,
            CancellationToken cancellationToken) =>
            (await handler.Handle(new ForgotPasswordCommand(request.Email), cancellationToken))
            .ToHttp(_ => TypedResults.Accepted((string?)null)))
            .WithName("ForgotPassword")
            .WithSummary("Sends a password-reset link if the account exists. Always answers 202.")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();

        guarded.MapPost("/reset-password", async (
            ResetPasswordRequest request,
            ICommandHandler<ResetPasswordCommand, Success> handler,
            CancellationToken cancellationToken) =>
            (await handler.Handle(new ResetPasswordCommand(request.Token, request.NewPassword), cancellationToken))
            .ToNoContent())
            .WithName("ResetPassword")
            .WithSummary("Sets a new password with the emailed token and signs out every session.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem();

        // The mobile app keeps its refresh token in secure storage, so these three take and return it in the body.
        // They never read or write the cookie, and the cookie endpoints never take a body token: script running in
        // the web app cannot turn the browser's cookie into a refresh token it can read.
        var mobile = auth.MapGroup("/mobile").WithTags("Auth (mobile)");

        mobile.MapPost("/login", async (
            MobileLoginRequest request,
            HttpContext http,
            ICommandHandler<LoginCommand, AuthTokens> handler,
            CancellationToken cancellationToken) =>
            MobileSignIn(await handler.Handle(
                new LoginCommand(request.Email, request.Password, MobileUserAgent(http, request.DeviceName)),
                cancellationToken)))
            .RequireRateLimiting(RateLimitingSetup.AuthPolicy)
            .WithName("MobileLogin")
            .WithSummary("Signs a staff user in on the mobile app and returns both tokens.")
            .Produces<MobileAuthResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        mobile.MapPost("/refresh", async (
            MobileRefreshRequest request,
            HttpContext http,
            ICommandHandler<RefreshSessionCommand, AuthTokens> handler,
            CancellationToken cancellationToken) =>
            MobileSignIn(await handler.Handle(new RefreshSessionCommand(request.RefreshToken, UserAgent(http)), cancellationToken)))
            .WithName("MobileRefreshSession")
            .WithSummary("Rotates the refresh token from the body and returns the new pair. Replaying an old one ends the session.")
            .Produces<MobileAuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        mobile.MapPost("/logout", async (
            MobileRefreshRequest request,
            ICommandHandler<LogoutCommand, Success> handler,
            CancellationToken cancellationToken) =>
            (await handler.Handle(new LogoutCommand(request.RefreshToken), cancellationToken)).ToNoContent())
            .WithName("MobileLogout")
            .WithSummary("Revokes the refresh-token family the given token belongs to.")
            .Produces(StatusCodes.Status204NoContent);

        auth.MapGet("/me", async (
            IQueryHandler<GetSessionQuery, SessionDto> handler,
            CancellationToken cancellationToken) =>
            (await handler.Handle(new GetSessionQuery(), cancellationToken)).ToOk())
            .RequireAuthorization()
            .WithName("GetSession")
            .WithSummary("Returns the signed-in user, their workspace and their permissions.")
            .Produces<SessionDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return api;
    }

    internal static IResult SignIn(HttpContext http, Result<AuthTokens> result)
    {
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var tokens = result.Value;
        http.Response.Cookies.Append(RefreshCookie, tokens.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = tokens.RefreshTokenExpiresAt,
            IsEssential = true,
        });
        return TypedResults.Ok(new AuthResponse(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.Session));
    }

    private static IResult MobileSignIn(Result<AuthTokens> result)
    {
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var tokens = result.Value;
        return TypedResults.Ok(new MobileAuthResponse(
            tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.RefreshToken, tokens.RefreshTokenExpiresAt, tokens.Session));
    }

    /// <summary>The app's user agent plus the device name the person sees, so the sign-in can be recognised later.</summary>
    private static string? MobileUserAgent(HttpContext http, string? deviceName) =>
        (UserAgent(http), string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim()) switch
        {
            (null, null) => null,
            (var agent, null) => agent,
            (null, var device) => device,
            (var agent, var device) => $"{agent}; {device}",
        };

    private static void ClearRefreshCookie(HttpContext http) =>
        http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = RefreshCookiePath });

    internal static string? UserAgent(HttpContext http) => http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua
        ? ua
        : null;
}
