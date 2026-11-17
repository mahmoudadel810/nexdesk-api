using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NexDesk.Api.Endpoints;
using NexDesk.Api.Realtime;
using NexDesk.Application.Billing;
using NexDesk.Infrastructure.Identity;

namespace NexDesk.Api.RateLimiting;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Requests per minute for one address that is not signed in to a workspace.</summary>
    public int AnonymousPerMinute { get; set; } = 120;

    /// <summary>Sign-in, sign-up and password requests per minute for one address.</summary>
    public int AuthPerMinute { get; set; } = 10;

    /// <summary>Anonymous writes, such as article feedback, per minute for one address.</summary>
    public int PublicWritePerMinute { get; set; } = 20;
}

public static class RateLimitingSetup
{
    /// <summary>Applied to endpoints that take credentials or send emails, where guessing must be slow.</summary>
    public const string AuthPolicy = "auth";

    /// <summary>Applied to endpoints anyone may write to.</summary>
    public const string PublicWritePolicy = "public-write";

    public const string TooManyRequestsCode = "too-many-requests";

    private const string PlanLimitItem = "NexDesk.PlanRequestsPerMinute";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddNexDeskRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitOptions>().BindConfiguration(RateLimitOptions.SectionName);
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = WriteProblem;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(GlobalPartition);
            limiter.AddPolicy(AuthPolicy, http => PerAddress(http, AuthPolicy, options => options.AuthPerMinute));
            limiter.AddPolicy(
                PublicWritePolicy, http => PerAddress(http, PublicWritePolicy, options => options.PublicWritePerMinute));
        });
        return services;
    }

    /// <summary>
    /// Looks up what the signed-in workspace's plan allows before the limiter runs, because the limiter
    /// itself cannot wait for a database.
    /// </summary>
    public static IApplicationBuilder UseNexDeskRateLimiting(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (Options(http).Enabled && StaffTenantId(http) is { } tenantId)
            {
                http.Items[PlanLimitItem] = await http.RequestServices
                    .GetRequiredService<IPlanRateLimits>()
                    .RequestsPerMinuteAsync(tenantId, http.RequestAborted);
            }

            await next(http);
        }).UseRateLimiter();

    private static RateLimitPartition<string> GlobalPartition(HttpContext http)
    {
        var options = Options(http);
        var path = http.Request.Path;
        if (!options.Enabled || path.StartsWithSegments("/health") || path.StartsWithSegments(NotificationsHub.Path))
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        // Each team member gets the allowance of the workspace's plan. The limit is part of the key, so
        // a plan change takes effect at once instead of when the old window happens to end.
        var person = http.User.Identity?.IsAuthenticated == true ? http.User.FindFirst(NexDeskClaims.Subject)?.Value : null;
        if (person is not null && StaffTenantId(http) is not null && http.Items[PlanLimitItem] is int planLimit)
        {
            return FixedWindow($"member:{person}:{planLimit}", planLimit);
        }

        return person is not null
            ? FixedWindow($"customer:{person}", options.AnonymousPerMinute)
            : FixedWindow($"address:{Address(http)}", options.AnonymousPerMinute);
    }

    private static RateLimitPartition<string> PerAddress(HttpContext http, string policy, Func<RateLimitOptions, int> limit)
    {
        var options = Options(http);
        return options.Enabled
            ? FixedWindow($"{policy}:{Address(http)}", limit(options))
            : RateLimitPartition.GetNoLimiter("unlimited");
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permitsPerMinute),
            Window = Window,
            QueueLimit = 0,
        });

    private static Guid? StaffTenantId(HttpContext http) =>
        http.User.FindFirst("aud")?.Value == JwtOptions.StaffAudience
        && Guid.TryParse(http.User.FindFirst(NexDeskClaims.TenantId)?.Value, out var tenantId)
            ? tenantId
            : null;

    private static string Address(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitOptions Options(HttpContext http) =>
        http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;

    private static async ValueTask WriteProblem(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : Window;
        http.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests. Wait a moment and try again.",
                Type = ResultExtensions.ProblemTypeBase + TooManyRequestsCode,
                Extensions = { ["code"] = TooManyRequestsCode },
            },
        });
    }
}
