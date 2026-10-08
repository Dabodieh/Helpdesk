using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Helpdesk.SharedKernel.Security;

public static class RequestLimits
{
    /// <summary>Sets the body-size limit for this request (before the body is read). A read-only feature (the body already started) is left alone.</summary>
    public static void LimitRequestBody(this HttpRequest request, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maxBytes;
        }
    }
}
