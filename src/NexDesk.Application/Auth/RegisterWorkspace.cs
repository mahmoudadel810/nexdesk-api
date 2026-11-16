using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Sla;
using NexDesk.Domain.Tenants;

namespace NexDesk.Application.Auth;

public sealed record RegisterWorkspaceCommand(
    string WorkspaceName,
    string? Slug,
    string FullName,
    string Email,
    string Password,
    string? Language,
    string? Timezone) : ICommand<RegisterWorkspaceResult>;

public sealed record RegisterWorkspaceResult(Guid TenantId, string Slug, string Email);

internal sealed class RegisterWorkspaceValidator : AbstractValidator<RegisterWorkspaceCommand>
{
    public RegisterWorkspaceValidator()
    {
        RuleFor(c => c.WorkspaceName).NotEmpty().MaximumLength(Tenant.NameMaxLength);
        RuleFor(c => c.Slug!)
            .Must(Tenant.IsValidSlug)
            .WithMessage("Use 3 to 40 lowercase letters, digits or hyphens.")
            .When(c => !string.IsNullOrEmpty(c.Slug));
        RuleFor(c => c.FullName).NotEmpty().MaximumLength(User.FullNameMaxLength);
        RuleFor(c => c.Email).ValidEmail();
        RuleFor(c => c.Password).StrongPassword();
        RuleFor(c => c.Language!)
            .Must(SupportedLanguages.All.Contains)
            .WithMessage("The language is not supported.")
            .When(c => c.Language is not null);
        RuleFor(c => c.Timezone!)
            .Must(id => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _))
            .WithMessage("The time zone is not recognised.")
            .When(c => c.Timezone is not null);
    }
}

internal sealed class RegisterWorkspaceHandler(
    IAppDbContext db,
    IIdentityDirectory directory,
    IPasswordHasher passwordHasher,
    ITenantScopeFactory tenantScopes,
    AuthEmails authEmails) : ICommandHandler<RegisterWorkspaceCommand, RegisterWorkspaceResult>
{
    private const int MaxSlugSuffix = 50;

    public async Task<Result<RegisterWorkspaceResult>> Handle(
        RegisterWorkspaceCommand command,
        CancellationToken cancellationToken)
    {
        if (await directory.EmailExistsAsync(command.Email, cancellationToken))
        {
            return AuthErrors.EmailTaken;
        }

        var slug = await ResolveSlug(command, cancellationToken);
        if (slug is null)
        {
            return AuthErrors.SlugTaken;
        }

        var language = SupportedLanguages.Normalize(command.Language);
        var tenant = Tenant.Create(command.WorkspaceName, slug, command.Timezone ?? "UTC", language);
        var owner = User.CreateStaff(tenant.Id, command.Email, command.FullName, Role.Owner, language);
        owner.SetPasswordHash(passwordHasher.Hash(command.Password));

        db.Tenants.Add(tenant);
        db.Users.Add(owner);
        var verification = authEmails.PrepareVerification(owner);

        using (tenantScopes.Begin(tenant.Id))
        {
            db.BusinessHours.AddRange(BusinessHoursDay.DefaultWeek());
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return await directory.EmailExistsAsync(command.Email, cancellationToken)
                    ? AuthErrors.EmailTaken
                    : AuthErrors.SlugTaken;
            }
        }

        authEmails.Send(verification);
        return new RegisterWorkspaceResult(tenant.Id, tenant.Slug, owner.Email);
    }

    private async Task<string?> ResolveSlug(RegisterWorkspaceCommand command, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(command.Slug))
        {
            return await SlugExists(command.Slug, cancellationToken) ? null : command.Slug;
        }

        var suggested = Tenant.SuggestSlug(command.WorkspaceName);
        var baseSlug = suggested.Length > 0 ? suggested : "workspace";
        if (suggested.Length > 0 && !await SlugExists(baseSlug, cancellationToken))
        {
            return baseSlug;
        }

        for (var suffix = 2; suffix <= MaxSlugSuffix; suffix++)
        {
            var candidate = $"{baseSlug[..Math.Min(baseSlug.Length, Tenant.SlugMaxLength - 3)]}-{suffix}";
            if (!await SlugExists(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        return null;
    }

    private Task<bool> SlugExists(string slug, CancellationToken cancellationToken) =>
        db.Tenants.AnyAsync(t => t.Slug == slug, cancellationToken);
}
