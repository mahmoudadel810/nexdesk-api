using System.Globalization;
using System.Text.RegularExpressions;
using NexDesk.Domain.Common;

namespace NexDesk.Domain.Tickets;

public enum CustomFieldType
{
    Text,
    Number,
    Dropdown,
    Checkbox,
    Date,
}

/// <summary>A ticket field defined by the tenant, such as "Order number" or "Product area".</summary>
public sealed partial class CustomField : AuditableEntity, ITenantOwned
{
    public const int KeyMaxLength = 40;
    public const int LabelMaxLength = 80;
    public const int TextValueMaxLength = 500;
    public const int MaxOptions = 50;
    public const int OptionMaxLength = 80;
    public const int MaxFieldsPerTenant = 30;

    private CustomField()
    {
    }

    public Guid TenantId { get; private set; }

    /// <summary>Stable identifier used in the API and stored on tickets. It never changes.</summary>
    public string Key { get; private set; } = null!;

    public string Label { get; private set; } = null!;

    public CustomFieldType Type { get; private set; }

    public List<string> Options { get; private set; } = [];

    public bool IsRequired { get; private set; }

    public bool IsActive { get; private set; }

    public int Position { get; private set; }

    public static CustomField Create(
        string key,
        string label,
        CustomFieldType type,
        IEnumerable<string>? options,
        bool isRequired,
        int position) => new()
        {
            Key = key,
            Label = label.Trim(),
            Type = type,
            Options = NormalizeOptions(type, options),
            IsRequired = isRequired,
            IsActive = true,
            Position = position,
        };

    public static bool IsValidKey(string key) => key.Length <= KeyMaxLength && KeyPattern().IsMatch(key);

    public void Update(string label, IEnumerable<string>? options, bool isRequired, bool isActive, int position)
    {
        Label = label.Trim();
        Options = NormalizeOptions(Type, options);
        IsRequired = isRequired;
        IsActive = isActive;
        Position = position;
    }

    /// <summary>Returns the value in its stored form, or null when it does not fit the field type.</summary>
    public string? Normalize(string value)
    {
        var trimmed = value.Trim();
        return Type switch
        {
            CustomFieldType.Text => trimmed.Length <= TextValueMaxLength ? trimmed : null,
            CustomFieldType.Number => decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : null,
            CustomFieldType.Checkbox => bool.TryParse(trimmed, out var flag) ? (flag ? "true" : "false") : null,
            CustomFieldType.Date => DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? trimmed
                : null,
            CustomFieldType.Dropdown => Options.Contains(trimmed) ? trimmed : null,
            _ => null,
        };
    }

    private static List<string> NormalizeOptions(CustomFieldType type, IEnumerable<string>? options) =>
        type == CustomFieldType.Dropdown
            ? [.. (options ?? []).Select(option => option.Trim()).Where(option => option.Length > 0).Distinct()]
            : [];

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex KeyPattern();
}

/// <summary>A named ticket filter. Private to its owner unless shared with the workspace.</summary>
public sealed class SavedView : AuditableEntity, ITenantOwned
{
    public const int NameMaxLength = 60;

    private SavedView()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid OwnerId { get; private set; }

    public string Name { get; private set; } = null!;

    public bool IsShared { get; private set; }

    /// <summary>The filter as JSON, in the shape the ticket list understands.</summary>
    public string FilterJson { get; private set; } = null!;

    public static SavedView Create(Guid ownerId, string name, bool isShared, string filterJson) => new()
    {
        OwnerId = ownerId,
        Name = name.Trim(),
        IsShared = isShared,
        FilterJson = filterJson,
    };

    public void Update(string name, bool isShared, string filterJson)
    {
        Name = name.Trim();
        IsShared = isShared;
        FilterJson = filterJson;
    }
}
