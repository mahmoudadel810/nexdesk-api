using NexDesk.Application.Common;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Sla;

namespace NexDesk.Application.Tenants;

/// <summary>
/// Adds the rows every workspace needs to the tenant currently in scope. The caller saves the context.
/// </summary>
public sealed class TenantProvisioner(IAppDbContext db, TimeProvider clock)
{
    public void AddDefaults()
    {
        db.BusinessHours.AddRange(BusinessHoursDay.DefaultWeek());
        db.SlaPolicies.AddRange(SlaPolicy.Defaults());
        db.Subscriptions.Add(Subscription.Start(clock.GetUtcNow()));
    }
}
