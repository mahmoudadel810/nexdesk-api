using NexDesk.Api.Auth;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Auth;
using NexDesk.Application.Common;
using NexDesk.Infrastructure.Identity;

namespace NexDesk.Api.Middleware;

/// <summary>Confines the request to the tenant named in the caller's token.</summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantScopeFactory tenantScopes)
    {
        if (context.Items.ContainsKey(AuthenticationSetup.TenantSuspendedItem))
        {
            await AuthErrors.TenantSuspended.ToProblem().ExecuteAsync(context);
            return;
        }

        if (Guid.TryParse(context.User.FindFirst(NexDeskClaims.TenantId)?.Value, out var tenantId))
        {
            using (tenantScopes.Begin(tenantId))
            {
                await next(context);
            }

            return;
        }

        await next(context);
    }
}
