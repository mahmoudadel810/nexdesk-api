using NexDesk.Application.Common;
using NexDesk.Domain.Sla;

namespace NexDesk.Application.Tenants;

/// <summary>
/// Adds the rows every workspace needs to the tenant currently in scope. The caller saves the context.
/// </summary>
public sealed class TenantProvisioner(IAppDbContext db)
{
    public void AddDefaults()
    {
        db.BusinessHours.AddRange(BusinessHoursDay.DefaultWeek());
        db.SlaPolicies.AddRange(SlaPolicy.Defaults());
    }
}
