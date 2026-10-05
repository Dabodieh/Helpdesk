using Helpdesk.SharedKernel.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Host.Errors;

/// <summary>Maps domain/authorization exceptions to RFC 9457 problem details (one place, so every module answers identically).</summary>
internal sealed class HelpdeskExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    private static ProblemDetails WithCode(ProblemDetails problem, string? code)
    {
        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        return problem;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            RequestValidationException v => new HttpValidationProblemDetails(v.Errors.ToDictionary(e => e.Key, e => e.Value))
            {
                Status = v.StatusCode,
                Title = v.Title,
            },
            HelpdeskException h => WithCode(new ProblemDetails { Status = h.StatusCode, Title = h.Title, Detail = h.Detail }, h.Code),
            DbUpdateConcurrencyException => WithCode(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Conflict", Detail = "The resource was changed by someone else. Reload and retry." }, ProblemCodes.VersionConflict),
            BadHttpRequestException => new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = "Bad request" },
            _ => null,
        };

        if (problem is null)
        {
            return false;
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
