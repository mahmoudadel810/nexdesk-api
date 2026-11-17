using Microsoft.EntityFrameworkCore;
using NexDesk.Domain.Audit;
using NexDesk.Domain.Automation;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Common;
using NexDesk.Domain.Customers;
using NexDesk.Domain.Developer;
using NexDesk.Domain.Identity;
using NexDesk.Domain.KnowledgeBase;
using NexDesk.Domain.Notifications;
using NexDesk.Domain.Sla;
using NexDesk.Domain.Teams;
using NexDesk.Domain.Tenants;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Common;

public interface IAppDbContext
{
    DbSet<Tenant> Tenants { get; }

    DbSet<User> Users { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<OneTimeToken> OneTimeTokens { get; }

    DbSet<Invitation> Invitations { get; }

    DbSet<Team> Teams { get; }

    DbSet<TeamMember> TeamMembers { get; }

    DbSet<BusinessHoursDay> BusinessHours { get; }

    DbSet<Customer> Customers { get; }

    DbSet<Ticket> Tickets { get; }

    DbSet<TicketMessage> TicketMessages { get; }

    DbSet<TicketEvent> TicketEvents { get; }

    DbSet<TicketWatcher> TicketWatchers { get; }

    DbSet<Attachment> Attachments { get; }

    DbSet<CustomField> CustomFields { get; }

    DbSet<SavedView> SavedViews { get; }

    DbSet<SlaPolicy> SlaPolicies { get; }

    DbSet<Holiday> Holidays { get; }

    DbSet<SlaBreach> SlaBreaches { get; }

    DbSet<CsatResponse> CsatResponses { get; }

    DbSet<Notification> Notifications { get; }

    DbSet<NotificationPreference> NotificationPreferences { get; }

    DbSet<KbCategory> KbCategories { get; }

    DbSet<KbArticle> KbArticles { get; }

    DbSet<AutomationRule> AutomationRules { get; }

    DbSet<AutomationExecution> AutomationExecutions { get; }

    DbSet<Plan> Plans { get; }

    DbSet<Subscription> Subscriptions { get; }

    DbSet<Invoice> Invoices { get; }

    DbSet<UsageCounter> UsageCounters { get; }

    DbSet<AuditLog> AuditLogs { get; }

    DbSet<ApiKey> ApiKeys { get; }

    DbSet<WebhookEndpoint> WebhookEndpoints { get; }

    DbSet<WebhookDelivery> WebhookDeliveries { get; }

    /// <summary>Forgets every tracked change, so an operation can be retried from fresh data.</summary>
    void DiscardChanges();

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>The tenant every query and write in the current scope is confined to.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}

public interface ITenantScopeFactory
{
    /// <summary>Confines the current scope to one tenant until the returned handle is disposed.</summary>
    IDisposable Begin(Guid tenantId);
}

public interface ICurrentUser
{
    Guid? UserId { get; }

    /// <summary>The API key a public API request is made with. Such requests have no user.</summary>
    Guid? ApiKeyId => null;

    /// <summary>Whether the user runs the platform rather than belonging to a workspace.</summary>
    bool IsPlatformAdmin => false;
}

/// <summary>Where the current request comes from. Empty outside a request, such as in background jobs.</summary>
public interface IRequestOrigin
{
    string? IpAddress { get; }
}

/// <summary>The portal customer making the request, if any.</summary>
public interface ICurrentCustomer
{
    Guid? CustomerId { get; }
}

/// <summary>Marks a class that is allowed to read across tenants by ignoring the tenant query filter.</summary>
public interface ICrossTenantQuery;

/// <summary>Staff lookups that run before a tenant is known, such as sign-up and login.</summary>
public interface IIdentityDirectory
{
    Task<User?> FindUserByEmailAsync(string email, CancellationToken cancellationToken);

    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    Task<InvitationLookup?> FindInvitationAsync(string tokenHash, CancellationToken cancellationToken);
}

public sealed record InvitationLookup(Invitation Invitation, Tenant Tenant);

/// <summary>Handles a domain event after the change that raised it has been saved.</summary>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task Handle(TEvent domainEvent, CancellationToken cancellationToken);
}

/// <summary>Runs just before tickets are written, inside the same save, to keep derived state consistent.</summary>
public interface ITicketSaveHook
{
    Task BeforeSaveAsync(IReadOnlyCollection<Ticket> tickets, CancellationToken cancellationToken);
}

/// <summary>Pushes live updates to connected browsers.</summary>
public interface IRealtimeNotifier
{
    /// <summary>Tells every staff member of a tenant that a ticket appeared or changed.</summary>
    Task TicketChangedAsync(Guid tenantId, Guid ticketId, string change, CancellationToken cancellationToken);

    /// <summary>Delivers a notification to one user's open sessions.</summary>
    Task NotificationAsync(Guid userId, object notification, CancellationToken cancellationToken);
}

public interface ITicketSearch
{
    /// <summary>Narrows tickets to those whose subject, conversation, number or requester match the term.</summary>
    IQueryable<Ticket> Matching(IQueryable<Ticket> tickets, string term);
}

public interface ITicketNumberAllocator
{
    /// <summary>Returns the next ticket number of the tenant in scope. Numbers are never reused.</summary>
    Task<int> NextAsync(CancellationToken cancellationToken);
}

public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string passwordHash, string password);
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    /// <summary>Signs an access token for a team member or the platform admin.</summary>
    /// <param name="user">Whose token it is.</param>
    /// <param name="sessionId">The refresh-token family the access token belongs to, carried as the <c>sid</c> claim.</param>
    AccessToken CreateStaffAccessToken(User user, Guid? sessionId = null);

    AccessToken CreateCustomerAccessToken(Customer customer);

    /// <summary>Creates a random URL-safe secret. Only its hash is ever stored.</summary>
    string CreateSecret();

    string HashSecret(string secret);
}

public interface ISessionStateCache
{
    void Invalidate(Guid userId);

    void InvalidateCustomer(Guid customerId);
}

public sealed record EmailMessage(
    string ToAddress,
    string ToName,
    string Template,
    string Language,
    IReadOnlyDictionary<string, string> Model);

public interface IEmailDispatcher
{
    void Enqueue(EmailMessage message);
}
