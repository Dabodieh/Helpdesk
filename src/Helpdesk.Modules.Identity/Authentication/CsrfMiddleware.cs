using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace Helpdesk.Modules.Identity.Authentication;

/// <summary>
/// Requires a valid antiforgery token (header <c>X-CSRF-TOKEN</c>, obtained from <c>GET /api/csrf</c>) on every unsafe request
/// that reaches an endpoint. Safe methods never mutate (contract), so they are not checked. Fetch the token after sign-in:
/// it is bound to the signed-in user.
/// </summary>
internal sealed class CsrfMiddleware(RequestDelegate next, IAntiforgery antiforgery)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));
        if (unsafeMethod && context.GetEndpoint() is not null)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                await Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid or missing CSRF token").ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}
