using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NexDesk.Application.Common;
using NexDesk.Infrastructure.Identity;

namespace NexDesk.Api.Auth;

/// <summary>Reads the signed-in person from the request. Staff and portal customers carry different audiences.</summary>
internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser, ICurrentCustomer
{
    public Guid? UserId => SubjectOf(JwtOptions.StaffAudience);

    public Guid? CustomerId => SubjectOf(JwtOptions.PortalAudience);

    public Guid? ApiKeyId
    {
        get
        {
            var user = accessor.HttpContext?.User;
            return user?.FindFirst("aud")?.Value == ApiKeyAuthenticationHandler.Audience
                && Guid.TryParse(user.FindFirst(ApiKeyAuthenticationHandler.ApiKeyIdClaim)?.Value, out var id)
                ? id
                : null;
        }
    }

    private Guid? SubjectOf(string audience)
    {
        var user = accessor.HttpContext?.User;
        return user?.FindFirst("aud")?.Value == audience
            && Guid.TryParse(user.FindFirst(NexDeskClaims.Subject)?.Value, out var id)
            ? id
            : null;
    }
}

/// <summary>The caller's address, after the trusted proxy's forwarded headers were applied.</summary>
internal sealed class HttpRequestOrigin(IHttpContextAccessor accessor) : IRequestOrigin
{
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}

public static class AuthenticationSetup
{
    public const string TenantSuspendedItem = "nexdesk.tenant-suspended";
    public const string PortalScheme = "Portal";

    public static IServiceCollection AddNexDeskAuthentication(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<CurrentUser>());
        services.AddScoped<ICurrentCustomer>(provider => provider.GetRequiredService<CurrentUser>());
        services.AddScoped<IRequestOrigin, HttpRequestOrigin>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer()
            .AddJwtBearer(PortalScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
        services.AddOptions<JwtBearerOptions>(PortalScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = JwtOptions.PortalAudience,
                    IssuerSigningKey = jwt.Value.CreateKey(),
                    NameClaimType = NexDeskClaims.Subject,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                bearer.Events = new JwtBearerEvents { OnTokenValidated = CheckCustomerSessionState };
            });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = JwtOptions.StaffAudience,
                    IssuerSigningKey = jwt.Value.CreateKey(),
                    NameClaimType = NexDeskClaims.Subject,
                    RoleClaimType = NexDeskClaims.Role,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = CheckSessionState,

                    // Browsers cannot set headers on WebSocket requests, so the hub takes the token from the query.
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }

    /// <summary>Requires an API key that was granted <paramref name="scope"/>.</summary>
    public static TBuilder RequireApiScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireClaim("aud", ApiKeyAuthenticationHandler.Audience)
            .RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, scope));

    /// <summary>Requires a signed-in staff user whose role grants <paramref name="permission"/>.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(NexDeskClaims.Permission, permission));

    private static async Task CheckCustomerSessionState(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        if (!Guid.TryParse(principal.FindFirst(NexDeskClaims.Subject)?.Value, out var customerId)
            || !int.TryParse(principal.FindFirst(NexDeskClaims.SecurityVersion)?.Value, out var securityVersion))
        {
            context.Fail("The token is missing required claims.");
            return;
        }

        var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionStateCache>();
        if (await sessions.CheckCustomerAsync(customerId, securityVersion, context.HttpContext.RequestAborted)
            != SessionState.Valid)
        {
            context.Fail("The session is no longer valid.");
        }
    }

    private static async Task CheckSessionState(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        if (!Guid.TryParse(principal.FindFirst(NexDeskClaims.Subject)?.Value, out var userId)
            || !int.TryParse(principal.FindFirst(NexDeskClaims.SecurityVersion)?.Value, out var securityVersion))
        {
            context.Fail("The token is missing required claims.");
            return;
        }

        var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionStateCache>();
        switch (await sessions.CheckAsync(userId, securityVersion, context.HttpContext.RequestAborted))
        {
            case SessionState.Invalid:
                context.Fail("The session is no longer valid.");
                break;
            case SessionState.TenantSuspended:
                context.HttpContext.Items[TenantSuspendedItem] = true;
                break;
        }
    }
}
