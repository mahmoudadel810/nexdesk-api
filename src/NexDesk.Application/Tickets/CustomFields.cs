using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Tickets;

public sealed record CustomFieldDto(
    Guid Id,
    string Key,
    string Label,
    CustomFieldType Type,
    IReadOnlyList<string> Options,
    bool IsRequired,
    bool IsActive,
    int Position)
{
    public static CustomFieldDto From(CustomField field) => new(
        field.Id, field.Key, field.Label, field.Type, field.Options, field.IsRequired, field.IsActive, field.Position);
}

public static class CustomFieldErrors
{
    public static readonly Error NotFound = Error.NotFound("custom-field-not-found", "The custom field was not found.");

    public static readonly Error KeyTaken = Error.Conflict("custom-field-key-taken", "A field with this key already exists.");

    public static readonly Error LimitReached = Error.Conflict(
        "custom-field-limit", $"A workspace can have at most {CustomField.MaxFieldsPerTenant} custom fields.");
}

/// <summary>Checks ticket custom field input against the tenant's field definitions.</summary>
public sealed class CustomFieldValues(IAppDbContext db)
{
    public sealed record Validated(
        IReadOnlyDictionary<string, string?> Values,
        IReadOnlyDictionary<string, string> Labels);

    /// <param name="input">Values keyed by field key. Null or blank clears a field.</param>
    /// <param name="enforceRequired">Whether required fields must end up with a value.</param>
    /// <param name="existing">Values the ticket already has, counted towards required fields.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async Task<Result<Validated>> ValidateAsync(
        IReadOnlyDictionary<string, string?>? input,
        bool enforceRequired,
        IReadOnlyDictionary<string, string>? existing,
        CancellationToken cancellationToken)
    {
        var fields = await db.CustomFields.AsNoTracking().Where(f => f.IsActive).ToListAsync(cancellationToken);
        var byKey = fields.ToDictionary(f => f.Key);
        var values = new Dictionary<string, string?>();

        foreach (var (key, raw) in input ?? new Dictionary<string, string?>())
        {
            if (!byKey.TryGetValue(key, out var field))
            {
                return TicketErrors.UnknownCustomField;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                values[key] = null;
                continue;
            }

            var normalized = field.Normalize(raw);
            if (normalized is null)
            {
                return TicketErrors.InvalidCustomFieldValue;
            }

            values[key] = normalized;
        }

        if (enforceRequired && fields.Any(f => f.IsRequired
            && (values.TryGetValue(f.Key, out var value) ? value is null : existing?.ContainsKey(f.Key) != true)))
        {
            return TicketErrors.RequiredCustomFieldMissing;
        }

        return new Validated(values, fields.ToDictionary(f => f.Key, f => f.Label));
    }
}

public sealed record ListCustomFieldsQuery : IQuery<IReadOnlyList<CustomFieldDto>>;

internal sealed class ListCustomFieldsHandler(IAppDbContext db)
    : IQueryHandler<ListCustomFieldsQuery, IReadOnlyList<CustomFieldDto>>
{
    public async Task<Result<IReadOnlyList<CustomFieldDto>>> Handle(
        ListCustomFieldsQuery query,
        CancellationToken cancellationToken) =>
        (await db.CustomFields.AsNoTracking().OrderBy(f => f.Position).ThenBy(f => f.Label).ToListAsync(cancellationToken))
        .Select(CustomFieldDto.From)
        .ToList();
}

public sealed record CreateCustomFieldCommand(
    string Key,
    string Label,
    CustomFieldType Type,
    IReadOnlyCollection<string>? Options,
    bool IsRequired,
    int Position) : ICommand<CustomFieldDto>;

internal static class CustomFieldRules
{
    public static void AddCommonRules<T>(
        this AbstractValidator<T> validator,
        Func<T, string> label,
        Func<T, IReadOnlyCollection<string>?> options)
    {
        validator.RuleFor(c => label(c)).NotEmpty().MaximumLength(CustomField.LabelMaxLength).OverridePropertyName("Label");
        validator.RuleFor(c => options(c))
            .Must(o => o is null || (o.Count <= CustomField.MaxOptions
                && o.All(option => option is not null && option.Trim().Length is > 0 and <= CustomField.OptionMaxLength)))
            .WithMessage($"Use at most {CustomField.MaxOptions} options of up to {CustomField.OptionMaxLength} characters each.")
            .OverridePropertyName("Options");
    }
}

internal sealed class CreateCustomFieldValidator : AbstractValidator<CreateCustomFieldCommand>
{
    public CreateCustomFieldValidator()
    {
        RuleFor(c => c.Key)
            .NotEmpty()
            .Must(CustomField.IsValidKey)
            .WithMessage("Start with a lowercase letter and use only lowercase letters, digits and underscores.");
        RuleFor(c => c.Type).IsInEnum();
        RuleFor(c => c.Options)
            .Must(options => options is { Count: > 0 })
            .When(c => c.Type == CustomFieldType.Dropdown)
            .WithMessage("A dropdown needs at least one option.");
        this.AddCommonRules(c => c.Label, c => c.Options);
    }
}

internal sealed class CreateCustomFieldHandler(IAppDbContext db) : ICommandHandler<CreateCustomFieldCommand, CustomFieldDto>
{
    public async Task<Result<CustomFieldDto>> Handle(CreateCustomFieldCommand command, CancellationToken cancellationToken)
    {
        if (await db.CustomFields.AnyAsync(f => f.Key == command.Key, cancellationToken))
        {
            return CustomFieldErrors.KeyTaken;
        }

        if (await db.CustomFields.CountAsync(cancellationToken) >= CustomField.MaxFieldsPerTenant)
        {
            return CustomFieldErrors.LimitReached;
        }

        var field = CustomField.Create(
            command.Key, command.Label, command.Type, command.Options, command.IsRequired, command.Position);
        db.CustomFields.Add(field);
        await db.SaveChangesAsync(cancellationToken);
        return CustomFieldDto.From(field);
    }
}

public sealed record UpdateCustomFieldCommand(
    Guid FieldId,
    string Label,
    IReadOnlyCollection<string>? Options,
    bool IsRequired,
    bool IsActive,
    int Position) : ICommand<CustomFieldDto>;

internal sealed class UpdateCustomFieldValidator : AbstractValidator<UpdateCustomFieldCommand>
{
    public UpdateCustomFieldValidator() => this.AddCommonRules(c => c.Label, c => c.Options);
}

internal sealed class UpdateCustomFieldHandler(IAppDbContext db) : ICommandHandler<UpdateCustomFieldCommand, CustomFieldDto>
{
    public async Task<Result<CustomFieldDto>> Handle(UpdateCustomFieldCommand command, CancellationToken cancellationToken)
    {
        var field = await db.CustomFields.FirstOrDefaultAsync(f => f.Id == command.FieldId, cancellationToken);
        if (field is null)
        {
            return CustomFieldErrors.NotFound;
        }

        if (field.Type == CustomFieldType.Dropdown && command.Options is not { Count: > 0 })
        {
            return Error.Validation("validation", "A dropdown needs at least one option.");
        }

        field.Update(command.Label, command.Options, command.IsRequired, command.IsActive, command.Position);
        await db.SaveChangesAsync(cancellationToken);
        return CustomFieldDto.From(field);
    }
}

public sealed record DeleteCustomFieldCommand(Guid FieldId) : ICommand<Success>;

internal sealed class DeleteCustomFieldHandler(IAppDbContext db) : ICommandHandler<DeleteCustomFieldCommand, Success>
{
    public async Task<Result<Success>> Handle(DeleteCustomFieldCommand command, CancellationToken cancellationToken)
    {
        var field = await db.CustomFields.FirstOrDefaultAsync(f => f.Id == command.FieldId, cancellationToken);
        if (field is null)
        {
            return CustomFieldErrors.NotFound;
        }

        // Values already stored on tickets are kept; they simply stop being shown.
        db.CustomFields.Remove(field);
        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}
