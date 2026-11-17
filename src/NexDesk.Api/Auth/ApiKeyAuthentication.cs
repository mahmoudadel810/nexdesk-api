using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using NexDesk.Application.Developer;
using NexDesk.Infrastructure.Identity;

namespace NexDesk.Api.Auth;

/// <summary>
/// Signs in an integration from the <c>X-Api-Key</c> header. The resulting identity carries the key's scopes
/// and its own audience, never staff permissions, so a key can only reach the public API.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyDirectory directory) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string Audience = "nexdesk-api-key";
    public const string ApiKeyIdClaim = "api_key_id";
    public const string ScopeClaim = "scope";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || values.ToString() is not { Length: > 0 } presented)
        {
            return AuthenticateResult.NoResult();
        }

        var identity = await directory.AuthenticateAsync(presented.Trim(), Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.Fail("The API key is invalid, expired or revoked.");
        }

        if (identity.TenantSuspended)
        {
            Context.Items[AuthenticationSetup.TenantSuspendedItem] = true;
        }

        var claims = new List<Claim>
        {
            new("aud", Audience),
            new(ApiKeyIdClaim, identity.ApiKeyId.ToString()),
            new(NexDeskClaims.TenantId, identity.TenantId.ToString()),
        };
        claims.AddRange(identity.Scopes.Select(scope => new Claim(ScopeClaim, scope)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ApiKeyIdClaim, null));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = SchemeName;
        return base.HandleChallengeAsync(properties);
    }
}
