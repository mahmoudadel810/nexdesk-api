using NexDesk.Domain.Common;
using NexDesk.Domain.Identity;

namespace NexDesk.Domain.Customers;

/// <summary>A person who asks for help. Unique per tenant by email address.</summary>
public sealed class Customer : AuditableEntity, ITenantOwned
{
    public const int FullNameMaxLength = 100;

    private Customer()
    {
    }

    public Guid TenantId { get; private set; }

    public string Email { get; private set; } = null!;

    public string FullName { get; private set; } = null!;

    public static Customer Create(string email, string? fullName)
    {
        var normalized = User.NormalizeEmail(email);
        return new Customer
        {
            Email = normalized,
            FullName = string.IsNullOrWhiteSpace(fullName) ? normalized[..normalized.IndexOf('@')] : fullName.Trim(),
        };
    }

    public void Rename(string fullName) => FullName = fullName.Trim();
}
