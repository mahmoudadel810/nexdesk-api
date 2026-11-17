using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexDesk.Application.Common;
using NexDesk.Domain.Common;
using NexDesk.Infrastructure.Tickets;

namespace NexDesk.Infrastructure.Seeding;

/// <summary>
/// A clock the seeder moves through the demo's history, so everything the application stamps with "now"
/// (creation times, SLA deadlines, audit entries) lands on the simulated moment.
/// </summary>
public sealed class SeedClock : TimeProvider
{
    private DateTimeOffset? _now;

    public override DateTimeOffset GetUtcNow() => _now ?? System.GetUtcNow();

    public void Set(DateTimeOffset now) => _now = now;

    public void Reset() => _now = null;
}

/// <summary>Who the seeder is acting as, so audit entries and timelines name a person rather than nobody.</summary>
public sealed class SeedActor : ICurrentUser, ICurrentCustomer
{
    private Actor _actor = Actor.System;

    public Guid? UserId => _actor.Type == ActorType.Staff ? _actor.Id : null;

    public Guid? ApiKeyId => _actor.Type == ActorType.ApiKey ? _actor.Id : null;

    public Guid? CustomerId => _actor.Type == ActorType.Customer ? _actor.Id : null;

    public bool IsPlatformAdmin => false;

    public void Set(Actor actor) => _actor = actor;
}

/// <summary>Seeded history has already happened: nothing may notify, email or post because of it.</summary>
internal sealed class SilentDomainEvents : IDomainEventDispatcher
{
    public Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class SilentEmails : IEmailDispatcher
{
    public void Enqueue(EmailMessage message)
    {
    }
}

internal sealed class SilentPushes : IPushDispatcher
{
    public void Enqueue(PushMessage message)
    {
    }
}

public static class SeedServiceCollectionExtensions
{
    /// <summary>
    /// Turns the host into a one-off seeding run: a movable clock, a settable actor, and no events or emails.
    /// Call it after every other registration so these replace the normal services.
    /// </summary>
    public static IServiceCollection AddDemoSeeding(this IServiceCollection services)
    {
        var clock = new SeedClock();
        services.AddSingleton(clock);
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        services.AddSingleton<SeedActor>();
        services.Replace(ServiceDescriptor.Scoped<ICurrentUser>(provider => provider.GetRequiredService<SeedActor>()));
        services.Replace(ServiceDescriptor.Scoped<ICurrentCustomer>(provider => provider.GetRequiredService<SeedActor>()));
        services.Replace(ServiceDescriptor.Scoped<IDomainEventDispatcher, SilentDomainEvents>());
        services.Replace(ServiceDescriptor.Scoped<IEmailDispatcher, SilentEmails>());
        services.Replace(ServiceDescriptor.Scoped<IPushDispatcher, SilentPushes>());
        services.AddScoped<DemoSeeder>();
        return services;
    }
}
