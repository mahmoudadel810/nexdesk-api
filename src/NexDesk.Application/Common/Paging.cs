using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace NexDesk.Application.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public interface IPagedRequest
{
    int Page { get; }

    int PageSize { get; }
}

public static class Paging
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public static void AddPagingRules<T>(this AbstractValidator<T> validator)
        where T : IPagedRequest
    {
        validator.RuleFor(request => request.Page).GreaterThanOrEqualTo(1);
        validator.RuleFor(request => request.PageSize).InclusiveBetween(1, MaxPageSize);
    }

    public static async Task<PagedResult<T>> ToPagedAsync<T>(
        this IQueryable<T> query,
        IPagedRequest request,
        CancellationToken cancellationToken)
    {
        var total = await query.CountAsync(cancellationToken);
        var items = total == 0
            ? []
            : await query
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToListAsync(cancellationToken);
        return new PagedResult<T>(items, request.Page, request.PageSize, total);
    }

    /// <summary>Splits a sort expression such as <c>-createdAt</c> into its field and direction.</summary>
    public static (string Field, bool Descending) ParseSort(string? sort, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(sort) ? fallback : sort.Trim();
        return value.StartsWith('-') ? (value[1..].ToLowerInvariant(), true) : (value.ToLowerInvariant(), false);
    }
}

public static class CurrentUserExtensions
{
    public static Guid RequireUserId(this ICurrentUser currentUser) =>
        currentUser.UserId ?? throw new InvalidOperationException("This operation requires a signed-in user.");
}
