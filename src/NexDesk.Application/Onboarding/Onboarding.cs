using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;

namespace NexDesk.Application.Onboarding;

public sealed record OnboardingStatusDto(
    bool Completed,
    int TeamCount,
    int MemberCount,
    int PendingInvitationCount);

internal static class OnboardingQueries
{
    public static async Task<OnboardingStatusDto> Load(
        IAppDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var completed = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenant.TenantId)
            .Select(t => t.OnboardingCompletedAt != null)
            .FirstAsync(cancellationToken);
        return new OnboardingStatusDto(
            completed,
            await db.Teams.CountAsync(cancellationToken),
            await db.Users.CountAsync(u => u.IsActive, cancellationToken),
            await db.Invitations.CountAsync(
                i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now,
                cancellationToken));
    }
}

public sealed record GetOnboardingQuery : IQuery<OnboardingStatusDto>;

internal sealed class GetOnboardingHandler(IAppDbContext db, ITenantContext tenant, TimeProvider clock)
    : IQueryHandler<GetOnboardingQuery, OnboardingStatusDto>
{
    public async Task<Result<OnboardingStatusDto>> Handle(GetOnboardingQuery query, CancellationToken cancellationToken) =>
        await OnboardingQueries.Load(db, tenant, clock, cancellationToken);
}

public sealed record CompleteOnboardingCommand : ICommand<OnboardingStatusDto>;

internal sealed class CompleteOnboardingHandler(IAppDbContext db, ITenantContext tenantContext, TimeProvider clock)
    : ICommandHandler<CompleteOnboardingCommand, OnboardingStatusDto>
{
    public async Task<Result<OnboardingStatusDto>> Handle(
        CompleteOnboardingCommand command,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantContext.TenantId, cancellationToken);
        tenant.CompleteOnboarding(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return await OnboardingQueries.Load(db, tenantContext, clock, cancellationToken);
    }
}
