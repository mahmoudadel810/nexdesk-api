using System.Security.Cryptography;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace NexDesk.Api.Admin;

/// <summary>
/// Lets a platform admin into the Hangfire dashboard. The dashboard is a server-rendered page the browser
/// opens directly, so it cannot send the bearer token the rest of the API uses. Instead the admin asks the
/// API for a ticket that works once within a minute; opening it swaps the ticket for a session cookie that
/// is sent only to the dashboard.
/// </summary>
public sealed class JobsDashboardAccess(IMemoryCache cache)
{
    public const string Path = "/hangfire";
    public const string CookieName = "nexdesk_jobs";
    private const string TicketParameter = "ticket";
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Returns the address that opens the dashboard for <paramref name="adminId"/>.</summary>
    public string IssueLink(Guid adminId)
    {
        var ticket = NewSecret();
        cache.Set(TicketKey(ticket), adminId, TicketLifetime);
        return $"{Path}?{TicketParameter}={ticket}";
    }

    public bool HasSession(string? session) =>
        session is { Length: > 0 } && cache.TryGetValue(SessionKey(session), out Guid _);

    /// <summary>Exchanges a ticket in the address for a session cookie, then reloads without the ticket.</summary>
    public async Task RedeemAsync(HttpContext http, Func<Task> next)
    {
        var ticket = http.Request.Query[TicketParameter].ToString();
        if (ticket.Length == 0)
        {
            await next();
            return;
        }

        if (!cache.TryGetValue(TicketKey(ticket), out Guid adminId))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        cache.Remove(TicketKey(ticket));
        var session = NewSecret();
        cache.Set(SessionKey(session), adminId, new MemoryCacheEntryOptions { SlidingExpiration = SessionLifetime });
        http.Response.Cookies.Append(CookieName, session, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = Path,
            MaxAge = SessionLifetime,
        });
        http.Response.Redirect(Path);
    }

    private static string NewSecret() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private static string TicketKey(string ticket) => $"jobs-ticket:{ticket}";

    private static string SessionKey(string session) => $"jobs-session:{session}";
}

internal sealed class JobsDashboardAuthorization(JobsDashboardAccess access) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) =>
        access.HasSession(context.GetHttpContext().Request.Cookies[JobsDashboardAccess.CookieName]);
}

public static class JobsDashboardSetup
{
    public static WebApplication MapJobsDashboard(this WebApplication app)
    {
        var access = app.Services.GetRequiredService<JobsDashboardAccess>();
        app.UseWhen(
            http => http.Request.Path.StartsWithSegments(JobsDashboardAccess.Path),
            dashboard => dashboard.Use((http, next) => access.RedeemAsync(http, () => next(http))));
        app.UseHangfireDashboard(JobsDashboardAccess.Path, new DashboardOptions
        {
            DashboardTitle = "NexDesk background jobs",
            AppPath = "/admin",
            Authorization = [new JobsDashboardAuthorization(access)],
            DisplayStorageConnectionString = false,
        });
        return app;
    }
}
