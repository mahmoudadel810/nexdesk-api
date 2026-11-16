using Microsoft.AspNetCore.Http.HttpResults;
using NexDesk.Application.Common;

namespace NexDesk.Api.Endpoints;

public static class ResultExtensions
{
    public const string ProblemTypeBase = "https://nexdesk.dev/problems/";

    public static ProblemHttpResult ToProblem(this Error error) => TypedResults.Problem(
        statusCode: error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        },
        title: error.Message,
        type: ProblemTypeBase + error.Code,
        extensions: new Dictionary<string, object?> { ["code"] = error.Code });

    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();

    public static IResult ToOk<T>(this Result<T> result) => result.ToHttp(value => TypedResults.Ok(value));

    public static IResult ToNoContent(this Result<Success> result) => result.ToHttp(_ => TypedResults.NoContent());
}
