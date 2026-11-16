using NexDesk.Application.Common;

namespace NexDesk.Infrastructure.Tenancy;

internal sealed class TenantContext : ITenantContext, ITenantScopeFactory
{
    public Guid? TenantId { get; private set; }

    public IDisposable Begin(Guid tenantId)
    {
        var previous = TenantId;
        TenantId = tenantId;
        return new Scope(this, previous);
    }

    private sealed class Scope(TenantContext context, Guid? previous) : IDisposable
    {
        public void Dispose() => context.TenantId = previous;
    }
}
