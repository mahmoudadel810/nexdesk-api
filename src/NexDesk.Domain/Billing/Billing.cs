using NexDesk.Domain.Common;

namespace NexDesk.Domain.Billing;

/// <summary>A subscription tier with its price and limits. A limit of null means unlimited.</summary>
public sealed class Plan
{
    public const string Free = "free";
    public const string Pro = "pro";
    public const string Business = "business";
    public const string Currency = "USD";

    private const long Gigabyte = 1024L * 1024 * 1024;

    private Plan()
    {
    }

    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public int PriceCentsMonthly { get; private set; }

    /// <summary>Team members who can work on tickets. Viewers do not count.</summary>
    public int? MaxAgents { get; private set; }

    public int? MaxTicketsPerMonth { get; private set; }

    public long MaxStorageBytes { get; private set; }

    public int? MaxAutomationRules { get; private set; }

    public bool ApiAccess { get; private set; }

    /// <summary>How many API requests each team member of a workspace on this plan may make per minute.</summary>
    public int RequestsPerMinute { get; private set; }

    public int Position { get; private set; }

    public bool IsFree => PriceCentsMonthly == 0;

    /// <summary>The plans every installation starts with.</summary>
    public static IReadOnlyList<Plan> Catalog { get; } =
    [
        new()
        {
            Code = Free,
            Name = "Free",
            PriceCentsMonthly = 0,
            MaxAgents = 3,
            MaxTicketsPerMonth = 100,
            MaxStorageBytes = Gigabyte,
            MaxAutomationRules = 3,
            ApiAccess = false,
            RequestsPerMinute = 240,
            Position = 0,
        },
        new()
        {
            Code = Pro,
            Name = "Pro",
            PriceCentsMonthly = 2900,
            MaxAgents = 10,
            MaxTicketsPerMonth = 2000,
            MaxStorageBytes = 20 * Gigabyte,
            MaxAutomationRules = 10,
            ApiAccess = false,
            RequestsPerMinute = 600,
            Position = 1,
        },
        new()
        {
            Code = Business,
            Name = "Business",
            PriceCentsMonthly = 9900,
            MaxAgents = null,
            MaxTicketsPerMonth = null,
            MaxStorageBytes = 200 * Gigabyte,
            MaxAutomationRules = null,
            ApiAccess = true,
            RequestsPerMinute = 1200,
            Position = 2,
        },
    ];
}

/// <summary>The card a simulated payment was made with. Only the brand and last four digits are kept.</summary>
public sealed record PaymentCard(string Brand, string Last4)
{
    /// <summary>A card number that always fails, so the declined path can be shown.</summary>
    public const string DeclinedNumber = "4000000000000002";

    /// <summary>Reads a card number, checking its length and Luhn checksum. Nothing is charged anywhere.</summary>
    public static PaymentCard? Parse(string? number)
    {
        var digits = new string((number ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (digits.Length is < 13 or > 19 || !digits.All(char.IsAsciiDigit) || !PassesLuhn(digits))
        {
            return null;
        }

        var brand = digits[0] switch
        {
            '4' => "Visa",
            '5' or '2' => "Mastercard",
            '3' => "American Express",
            _ => "Card",
        };
        return new PaymentCard(brand, digits[^4..]);
    }

    public static bool IsDeclined(string? number) =>
        new string((number ?? string.Empty).Where(char.IsAsciiDigit).ToArray()) == DeclinedNumber;

    public static bool IsExpired(int month, int year, DateTimeOffset now) =>
        month is < 1 or > 12 || year < now.Year || (year == now.Year && month < now.Month);

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        for (var index = 0; index < digits.Length; index++)
        {
            var digit = digits[digits.Length - 1 - index] - '0';
            if (index % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
        }

        return sum % 10 == 0;
    }
}

/// <summary>The plan a workspace is on. Every workspace has exactly one.</summary>
public sealed class Subscription : AuditableEntity, ITenantOwned
{
    private Subscription()
    {
    }

    public Guid TenantId { get; private set; }

    public string PlanCode { get; private set; } = null!;

    public DateTimeOffset CurrentPeriodStart { get; private set; }

    /// <summary>When the paid period ends and the plan renews. Null on the free plan.</summary>
    public DateTimeOffset? CurrentPeriodEnd { get; private set; }

    public string? CardBrand { get; private set; }

    public string? CardLast4 { get; private set; }

    public static Subscription Start(DateTimeOffset now) => new()
    {
        PlanCode = Plan.Free,
        CurrentPeriodStart = now,
    };

    /// <summary>Moves to another plan. A paid plan starts a fresh one-month period paid with the card.</summary>
    public void ChangeTo(Plan plan, PaymentCard? card, DateTimeOffset now)
    {
        PlanCode = plan.Code;
        CurrentPeriodStart = now;
        CurrentPeriodEnd = plan.IsFree ? null : now.AddMonths(1);
        CardBrand = plan.IsFree ? null : card?.Brand;
        CardLast4 = plan.IsFree ? null : card?.Last4;
    }

    public bool IsDueForRenewal(DateTimeOffset now) => CurrentPeriodEnd is { } end && end <= now;

    /// <summary>Starts the next period where the last one ended.</summary>
    public void Renew()
    {
        if (CurrentPeriodEnd is { } end)
        {
            CurrentPeriodStart = end;
            CurrentPeriodEnd = end.AddMonths(1);
        }
    }
}

public enum InvoiceReason
{
    PlanChange,
    Renewal,
}

/// <summary>A paid bill for one month of a plan. Payments are simulated, so an invoice is paid when issued.</summary>
public sealed class Invoice : Entity, ITenantOwned
{
    public const int BilledToMaxLength = 254;

    private Invoice()
    {
    }

    public Guid TenantId { get; private set; }

    /// <summary>Counts up per workspace, starting at 1.</summary>
    public int Number { get; private set; }

    public string PlanCode { get; private set; } = null!;

    public string PlanName { get; private set; } = null!;

    public InvoiceReason Reason { get; private set; }

    public int AmountCents { get; private set; }

    public string Currency { get; private set; } = Plan.Currency;

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset PaidAt { get; private set; }

    public DateTimeOffset PeriodStart { get; private set; }

    public DateTimeOffset PeriodEnd { get; private set; }

    public string BilledToName { get; private set; } = null!;

    public string BilledToEmail { get; private set; } = null!;

    public string? CardBrand { get; private set; }

    public string? CardLast4 { get; private set; }

    public string DisplayNumber => FormatNumber(Number);

    public static string FormatNumber(int number) =>
        $"INV-{number.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)}";

    public static Invoice Issue(
        int number,
        Plan plan,
        Subscription subscription,
        InvoiceReason reason,
        string billedToName,
        string billedToEmail,
        DateTimeOffset now) => new()
        {
            Number = number,
            PlanCode = plan.Code,
            PlanName = plan.Name,
            Reason = reason,
            AmountCents = plan.PriceCentsMonthly,
            IssuedAt = now,
            PaidAt = now,
            PeriodStart = subscription.CurrentPeriodStart,
            PeriodEnd = subscription.CurrentPeriodEnd ?? subscription.CurrentPeriodStart.AddMonths(1),
            BilledToName = billedToName,
            BilledToEmail = billedToEmail,
            CardBrand = subscription.CardBrand,
            CardLast4 = subscription.CardLast4,
        };
}

/// <summary>What a workspace consumed in one calendar month (UTC).</summary>
public sealed class UsageCounter : Entity, ITenantOwned
{
    private UsageCounter()
    {
    }

    public Guid TenantId { get; private set; }

    /// <summary>The month as a number, for example 202611.</summary>
    public int Period { get; private set; }

    /// <summary>Tickets opened in the month. Deleting a ticket does not give the allowance back.</summary>
    public int TicketsCreated { get; private set; }

    public static int PeriodOf(DateTimeOffset moment) => (moment.UtcDateTime.Year * 100) + moment.UtcDateTime.Month;
}
