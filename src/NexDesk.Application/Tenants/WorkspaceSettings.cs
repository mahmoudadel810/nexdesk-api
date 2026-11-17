using System.Globalization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Billing;
using NexDesk.Application.Common;
using NexDesk.Domain.Tenants;

namespace NexDesk.Application.Tenants;

/// <summary>How a workspace presents itself: its name, region, language and help centre branding.</summary>
public sealed record WorkspaceSettingsDto(
    string Name,
    string Slug,
    string Timezone,
    string DefaultLanguage,
    string? BrandColor,
    string? PortalWelcomeText,
    string? LogoUrl)
{
    public static WorkspaceSettingsDto From(Tenant tenant) => new(
        tenant.Name,
        tenant.Slug,
        tenant.Timezone,
        tenant.DefaultLanguage,
        tenant.BrandColor,
        tenant.PortalWelcomeText,
        LogoUrlOf(tenant));

    /// <summary>
    /// The public address of the logo. It carries the time of the last change, so it can be cached for long
    /// and a new logo still shows at once.
    /// </summary>
    public static string? LogoUrlOf(Tenant tenant) =>
        tenant.LogoFileKey is null
            ? null
            : $"/api/v1/portal/{tenant.Slug}/logo?v={tenant.LogoUpdatedAt!.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
}

public sealed record LogoContent(string ContentType, Stream Content);

public static class SettingsErrors
{
    public static readonly Error LogoTooLarge = Error.Validation(
        "logo-too-large", "The logo must be at most 512 KB.");

    public static readonly Error LogoType = Error.Validation(
        "logo-type", "Upload the logo as a PNG, JPEG or WebP image.");

    public static readonly Error NoLogo = Error.NotFound("logo-not-found", "This workspace has no logo.");
}

/// <summary>Recognises an image by its first bytes, which a renamed file cannot fake.</summary>
public static class LogoImage
{
    private static readonly (string ContentType, string Extension, byte[] Signature, int Offset)[] Formats =
    [
        ("image/png", "png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0),
        ("image/jpeg", "jpg", [0xFF, 0xD8, 0xFF], 0),
        ("image/webp", "webp", "WEBP"u8.ToArray(), 8),
    ];

    public static (string ContentType, string Extension)? Detect(ReadOnlySpan<byte> header)
    {
        foreach (var (contentType, extension, signature, offset) in Formats)
        {
            if (header.Length >= offset + signature.Length && header.Slice(offset, signature.Length).SequenceEqual(signature)
                && (contentType != "image/webp" || header[..4].SequenceEqual("RIFF"u8)))
            {
                return (contentType, extension);
            }
        }

        return null;
    }
}

public sealed record GetWorkspaceSettingsQuery : IQuery<WorkspaceSettingsDto>;

internal sealed class GetWorkspaceSettingsHandler(IAppDbContext db, ITenantContext tenant)
    : IQueryHandler<GetWorkspaceSettingsQuery, WorkspaceSettingsDto>
{
    public async Task<Result<WorkspaceSettingsDto>> Handle(GetWorkspaceSettingsQuery query, CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenant.TenantId, cancellationToken);
        return WorkspaceSettingsDto.From(workspace);
    }
}

public sealed record UpdateWorkspaceSettingsCommand(
    string Name,
    string Timezone,
    string DefaultLanguage,
    string? BrandColor,
    string? PortalWelcomeText) : ICommand<WorkspaceSettingsDto>;

internal sealed class UpdateWorkspaceSettingsValidator : AbstractValidator<UpdateWorkspaceSettingsCommand>
{
    public UpdateWorkspaceSettingsValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Tenant.NameMaxLength);
        RuleFor(c => c.Timezone)
            .NotEmpty()
            .Must(id => id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _))
            .WithMessage("The time zone is not recognised.");
        RuleFor(c => c.DefaultLanguage).Must(language => Tenant.Languages.Contains(language))
            .WithMessage($"The language must be one of: {string.Join(", ", Tenant.Languages)}.");
        RuleFor(c => c.BrandColor).Must(Tenant.IsValidBrandColor)
            .WithMessage("Enter the colour as # followed by six hexadecimal digits, such as #2e7d32.");
        RuleFor(c => c.PortalWelcomeText).MaximumLength(Tenant.WelcomeTextMaxLength);
    }
}

internal sealed class UpdateWorkspaceSettingsHandler(IAppDbContext db, ITenantContext tenant)
    : ICommandHandler<UpdateWorkspaceSettingsCommand, WorkspaceSettingsDto>
{
    public async Task<Result<WorkspaceSettingsDto>> Handle(UpdateWorkspaceSettingsCommand command, CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.FirstAsync(t => t.Id == tenant.TenantId, cancellationToken);
        workspace.UpdateSettings(
            command.Name, command.Timezone, command.DefaultLanguage, command.BrandColor, command.PortalWelcomeText);
        await db.SaveChangesAsync(cancellationToken);
        return WorkspaceSettingsDto.From(workspace);
    }
}

public sealed record UploadLogoCommand(long SizeBytes, Stream Content) : ICommand<WorkspaceSettingsDto>;

internal sealed class UploadLogoHandler(
    IAppDbContext db,
    ITenantContext tenant,
    IFileStorage storage,
    IEntitlementService entitlements,
    TimeProvider clock) : ICommandHandler<UploadLogoCommand, WorkspaceSettingsDto>
{
    public async Task<Result<WorkspaceSettingsDto>> Handle(UploadLogoCommand command, CancellationToken cancellationToken)
    {
        if (command.SizeBytes is <= 0 or > Tenant.LogoMaxSizeBytes)
        {
            return SettingsErrors.LogoTooLarge;
        }

        using var buffer = new MemoryStream();
        await command.Content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length is 0 or > Tenant.LogoMaxSizeBytes)
        {
            return SettingsErrors.LogoTooLarge;
        }

        if (LogoImage.Detect(buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 16))) is not { } image)
        {
            return SettingsErrors.LogoType;
        }

        var workspace = await db.Tenants.FirstAsync(t => t.Id == tenant.TenantId, cancellationToken);
        if (await entitlements.CanStore(buffer.Length - workspace.LogoSizeBytes, cancellationToken) is { } limit)
        {
            return limit;
        }

        var key = $"{workspace.Id:N}/branding/logo-{Guid.CreateVersion7():N}.{image.Extension}";
        buffer.Position = 0;
        await storage.SaveAsync(key, buffer, cancellationToken);
        var previous = workspace.ReplaceLogo(key, image.ContentType, buffer.Length, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        if (previous is not null)
        {
            await storage.DeleteAsync(previous, cancellationToken);
        }

        return WorkspaceSettingsDto.From(workspace);
    }
}

public sealed record RemoveLogoCommand : ICommand<WorkspaceSettingsDto>;

internal sealed class RemoveLogoHandler(IAppDbContext db, ITenantContext tenant, IFileStorage storage, TimeProvider clock)
    : ICommandHandler<RemoveLogoCommand, WorkspaceSettingsDto>
{
    public async Task<Result<WorkspaceSettingsDto>> Handle(RemoveLogoCommand command, CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.FirstAsync(t => t.Id == tenant.TenantId, cancellationToken);
        var previous = workspace.RemoveLogo(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        if (previous is not null)
        {
            await storage.DeleteAsync(previous, cancellationToken);
        }

        return WorkspaceSettingsDto.From(workspace);
    }
}

/// <summary>The logo of an active workspace, for its help centre and anyone who links to it.</summary>
public sealed record GetPortalLogoQuery(string Slug) : IQuery<LogoContent>;

internal sealed class GetPortalLogoHandler(IAppDbContext db, IFileStorage storage) : IQueryHandler<GetPortalLogoQuery, LogoContent>
{
    public async Task<Result<LogoContent>> Handle(GetPortalLogoQuery query, CancellationToken cancellationToken)
    {
        var logo = await db.Tenants.AsNoTracking()
            .Where(t => t.Slug == query.Slug && t.Status == TenantStatus.Active && t.LogoFileKey != null)
            .Select(t => new { t.LogoFileKey, t.LogoContentType })
            .FirstOrDefaultAsync(cancellationToken);
        if (logo is null || await storage.OpenReadAsync(logo.LogoFileKey!, cancellationToken) is not { } content)
        {
            return SettingsErrors.NoLogo;
        }

        return new LogoContent(logo.LogoContentType!, content);
    }
}
