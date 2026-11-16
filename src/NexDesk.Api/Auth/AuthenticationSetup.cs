using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NexDesk.Application.Common;
using NexDesk.Infrastructure.Identity;

namespace NexDesk.Api.Auth;

internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirst(NexDeskClaims.Subject)?.Value, out var id) ? id : null;
}

public static class AuthenticationSetup
{
    public const string TenantSuspendedItem = "nexdesk.tenant-suspended";

    public static IServiceCollection AddNexDeskAuthentication(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
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
                bearer.Events = new JwtBearerEvents { OnTokenValidated = CheckSessionState };
            });

        services.AddAuthorization();
        return services;
    }

    /// <summary>Requires a signed-in staff user whose role grants <paramref name="permission"/>.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(NexDeskClaims.Permission, permission));

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
