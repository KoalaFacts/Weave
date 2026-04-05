namespace Weave.Silo.Api;

internal static class ResultExtensions
{
    internal static IResult NotFound(string detail) =>
        Results.Problem(statusCode: 404, title: "Not Found", detail: detail);

    internal static IResult Conflict(string detail) =>
        Results.Problem(statusCode: 409, title: "Conflict", detail: detail);

    internal static IResult ValidationFailed(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, title: "Validation Failed");

    internal static IResult ServerError() =>
        Results.Problem(statusCode: 500, title: "Internal Server Error", detail: "An unexpected error occurred.");
}
