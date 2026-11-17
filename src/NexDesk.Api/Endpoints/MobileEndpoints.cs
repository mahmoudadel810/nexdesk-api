using Microsoft.Extensions.Options;
using NexDesk.Application.Common;

namespace NexDesk.Api.Endpoints;

public sealed class MobileOptions
{
    public const string SectionName = "Mobile";

    /// <summary>The oldest Android build number that still works with this API.</summary>
    public int MinimumAndroidBuild { get; set; } = 1;

    /// <summary>The oldest iOS build number that still works with this API.</summary>
    public int MinimumIosBuild { get; set; } = 1;
}

public sealed record MobileMinimumBuild(int Android, int Ios);

/// <summary>What the app checks on start: whether it is too old, and where the web app is for web-only pages.</summary>
public sealed record MobileConfigDto(MobileMinimumBuild MinimumBuild, string WebBaseUrl);

public static class MobileEndpoints
{
    public static RouteGroupBuilder MapMobileEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/mobile/config", (IOptions<MobileOptions> mobile, IOptions<AppOptions> app) =>
            TypedResults.Ok(new MobileConfigDto(
                new MobileMinimumBuild(mobile.Value.MinimumAndroidBuild, mobile.Value.MinimumIosBuild),
                app.Value.WebBaseUrl.TrimEnd('/'))))
            .AllowAnonymous()
            .WithTags("Mobile")
            .WithName("GetMobileConfig")
            .WithSummary("Returns the oldest app builds the API still supports, and the web app's address.")
            .Produces<MobileConfigDto>();

        return api;
    }
}
