using System.Text.Json.Serialization;
using NexDesk.Api.Auth;
using NexDesk.Api.Endpoints;
using NexDesk.Api.Middleware;
using NexDesk.Application;
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

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi("v1");

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

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options => options.GetLevel = (context, _, exception) =>
    exception is null && context.Request.Path.StartsWithSegments("/health")
        ? Serilog.Events.LogEventLevel.Verbose
        : Serilog.Events.LogEventLevel.Information);
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
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

await app.RunAsync();

public sealed record ApiMetadata(string Name, string Version);
