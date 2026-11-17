using Microsoft.EntityFrameworkCore;
using NexDesk.Domain.Common;

namespace NexDesk.Application.Common;

/// <summary>Looks up the display names of the people and integrations behind a set of actions.</summary>
public static class ActorNames
{
    public static async Task<Dictionary<Actor, string>> Resolve(
        IAppDbContext db,
        IEnumerable<Actor> actors,
        CancellationToken cancellationToken)
    {
        var distinct = actors.Where(a => a.Id is not null).Distinct().ToList();
        var staffIds = IdsOf(distinct, ActorType.Staff);
        var customerIds = IdsOf(distinct, ActorType.Customer);
        var apiKeyIds = IdsOf(distinct, ActorType.ApiKey);

        var names = new Dictionary<Actor, string>();
        if (staffIds.Count > 0)
        {
            foreach (var user in await db.Users.AsNoTracking().Where(u => staffIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName }).ToListAsync(cancellationToken))
            {
                names[Actor.Staff(user.Id)] = user.FullName;
            }
        }

        if (customerIds.Count > 0)
        {
            foreach (var customer in await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id))
                .Select(c => new { c.Id, c.FullName }).ToListAsync(cancellationToken))
            {
                names[Actor.Customer(customer.Id)] = customer.FullName;
            }
        }

        if (apiKeyIds.Count > 0)
        {
            foreach (var key in await db.ApiKeys.AsNoTracking().Where(k => apiKeyIds.Contains(k.Id))
                .Select(k => new { k.Id, k.Name }).ToListAsync(cancellationToken))
            {
                names[Actor.ApiKey(key.Id)] = key.Name;
            }
        }

        return names;
    }

    public static async Task<string?> Resolve(IAppDbContext db, Actor actor, CancellationToken cancellationToken) =>
        (await Resolve(db, [actor], cancellationToken)).GetValueOrDefault(actor);

    private static List<Guid> IdsOf(IEnumerable<Actor> actors, ActorType type) =>
        actors.Where(a => a.Type == type).Select(a => a.Id!.Value).ToList();
}
