using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexDesk.Application.Common;
using NexDesk.Domain.Common;
using NexDesk.Infrastructure.Tickets;

namespace NexDesk.Infrastructure.Persistence;

/// <summary>Lets <c>dotnet ef</c> build the model without a running database or host.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=nexdesk_design")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options, new NoContext(), new NoContext(), TimeProvider.System, new NoContext());
    }

    private sealed class NoContext : ITenantContext, ICurrentUser, IDomainEventDispatcher
    {
        public Guid? TenantId => null;

        public Guid? UserId => null;

        public Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
