using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using NexDesk.Api.Auth;
using NexDesk.Api.Endpoints;
using NexDesk.Api.Middleware;
using NexDesk.Api.OpenApi;
using NexDesk.Api.RateLimiting;
using NexDesk.Api.Realtime;
using NexDesk.Application;
using NexDesk.Application.Common;
using NexDesk.Infrastructure;
using NexDesk.Infrastructure.Persistence;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "NexDesk.Api"));

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddNexDeskAuthentication();
builder.Services.AddOptions<InboundOptions>().BindConfiguration(InboundOptions.SectionName)
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSignalR().AddJsonProtocol(options =>
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddNexDeskOpenApi();
builder.Services.AddNexDeskRateLimiting();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // The reverse proxy runs next to the API on a private network. A forwarded address is only believed
    // when it comes from there, so a caller cannot dodge the rate limit by inventing one.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var network in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" })
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("postgres", tags: [HealthEndpoints.ReadyTag]);

var tracing = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("nexdesk-api"))
    .WithTracing(tracer => tracer
        .AddAspNetCoreInstrumentation(options => options.Filter = context =>
            !context.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation()
        .AddNpgsql());

if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    tracing.UseOtlpExporter();
}

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await app.Services.MigrateDatabaseAsync();
}

app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options => options.GetLevel = (context, _, exception) =>
    exception is null && context.Request.Path.StartsWithSegments("/health")
        ? Serilog.Events.LogEventLevel.Verbose
        : Serilog.Events.LogEventLevel.Information);
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseNexDeskRateLimiting();
app.UseAuthorization();

app.MapOpenApi();
app.MapScalarApiReference("/docs", options => options.WithTitle("NexDesk API"));

app.MapHealthEndpoints();

var v1 = app.MapGroup("/api/v1");
v1.MapGet("/meta", () => TypedResults.Ok(new ApiMetadata("NexDesk API", "v1")))
    .WithName("GetApiMetadata")
    .WithSummary("Returns the API name and version.")
    .WithTags("Meta");
v1.MapAuthEndpoints();
v1.MapWorkspaceEndpoints();
v1.MapTicketEndpoints();
v1.MapSlaEndpoints();
v1.MapNotificationEndpoints();
v1.MapKnowledgeBaseEndpoints();
v1.MapAutomationEndpoints();
v1.MapBillingEndpoints();
v1.MapPortalEndpoints();
app.MapHub<NotificationsHub>(NotificationsHub.Path);

await app.RunAsync();

public sealed record ApiMetadata(string Name, string Version);
