using System.Diagnostics.CodeAnalysis;

namespace NexDesk.Application.Common;

public enum ErrorKind
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,

    /// <summary>The workspace's plan does not allow it. Upgrading would.</summary>
    LimitReached,
}

/// <summary>A failure a caller can act on. <see cref="Code"/> is a stable slug used as the problem type.</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static Error Unauthorized(string code, string message) => new(ErrorKind.Unauthorized, code, message);

    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static Error LimitReached(string code, string message) => new(ErrorKind.LimitReached, code, message);
}

public readonly record struct Success;

public sealed class Result<T>
{
    private Result(T? value, Error? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public Error? Error { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static implicit operator Result<T>(T value) => new(value, null);

    public static implicit operator Result<T>(Error error) => new(default, error);
}
