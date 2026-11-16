using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;

namespace NexDesk.Application.Tickets;

internal static class Concurrency
{
    private const int MaxAttempts = 12;

    /// <summary>
    /// Runs an operation that does not depend on what the caller last saw (a reply, a bulk change) and
    /// repeats it from fresh data when another writer changed the same ticket in the meantime.
    /// </summary>
    public static async Task<Result<T>> RetryAsync<T>(IAppDbContext db, Func<Task<Result<T>>> attempt)
    {
        for (var attempts = 1; ; attempts++)
        {
            try
            {
                return await attempt();
            }
            catch (DbUpdateConcurrencyException) when (attempts < MaxAttempts)
            {
                db.DiscardChanges();

                // A short random pause keeps a burst of writers from colliding again in lockstep.
                await Task.Delay(Random.Shared.Next(5, 20) * attempts);
            }
        }
    }
}
