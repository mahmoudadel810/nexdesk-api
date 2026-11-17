using Microsoft.AspNetCore.Mvc;
using NexDesk.Api.Auth;
using NexDesk.Application.Common;
using NexDesk.Application.Tenants;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;

namespace NexDesk.Api.Endpoints;

/// <param name="Name">The workspace's name, shown to the team and in the help centre.</param>
/// <param name="Timezone">An IANA time zone such as Europe/London. SLA deadlines and reports follow it.</param>
/// <param name="DefaultLanguage">en or ar: the language new customers and emails start in.</param>
/// <param name="BrandColor">The help centre's accent colour as #rrggbb. Leave empty for the default.</param>
/// <param name="PortalWelcomeText">Up to 1,000 characters greeting customers on the help centre home page.</param>
public sealed record UpdateSettingsRequest(
    string Name,
    string Timezone,
    string DefaultLanguage,
    string? BrandColor = null,
    string? PortalWelcomeText = null);

public static class SettingsEndpoints
{
    public static RouteGroupBuilder MapSettingsEndpoints(this RouteGroupBuilder api)
    {
        var settings = api.MapGroup("/settings").WithTags("Settings").RequirePermission(Permissions.SettingsManage);

        settings.MapGet("/", async (IQueryHandler<GetWorkspaceSettingsQuery, WorkspaceSettingsDto> handler, CancellationToken ct) =>
            (await handler.Handle(new GetWorkspaceSettingsQuery(), ct)).ToOk())
            .WithName("GetSettings")
            .WithSummary("Returns the workspace's name, time zone, default language and help centre branding.")
            .Produces<WorkspaceSettingsDto>();

        settings.MapPut("/", async (
            UpdateSettingsRequest request,
            ICommandHandler<UpdateWorkspaceSettingsCommand, WorkspaceSettingsDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new UpdateWorkspaceSettingsCommand(
                    request.Name,
                    request.Timezone,
                    request.DefaultLanguage,
                    string.IsNullOrWhiteSpace(request.BrandColor) ? null : request.BrandColor.Trim(),
                    request.PortalWelcomeText),
                ct)).ToOk())
            .WithName("UpdateSettings")
            .WithSummary("Changes the workspace's name, time zone, default language, accent colour and welcome text.")
            .Produces<WorkspaceSettingsDto>()
            .ProducesValidationProblem();

        settings.MapPost("/logo", async (
            IFormFile file,
            ICommandHandler<UploadLogoCommand, WorkspaceSettingsDto> handler,
            CancellationToken ct) =>
        {
            await using var content = file.OpenReadStream();
            return (await handler.Handle(new UploadLogoCommand(file.Length, content), ct)).ToOk();
        })
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(Tenant.LogoMaxSizeBytes + (16 * 1024)))
            .WithName("UploadLogo")
            .WithSummary(
                "Replaces the logo with a PNG, JPEG or WebP image of up to 512 KB, sent as the form field \"file\". "
                + "The type is read from the file's content, not its name.")
            .Produces<WorkspaceSettingsDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status402PaymentRequired);

        settings.MapDelete("/logo", async (ICommandHandler<RemoveLogoCommand, WorkspaceSettingsDto> handler, CancellationToken ct) =>
            (await handler.Handle(new RemoveLogoCommand(), ct)).ToOk())
            .WithName("RemoveLogo")
            .WithSummary("Removes the logo; the help centre shows the workspace name instead.")
            .Produces<WorkspaceSettingsDto>();

        return api;
    }
}
