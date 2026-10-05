using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Helpdesk.Host.Tests.Infrastructure;

public sealed record ApiResponse(HttpStatusCode Status, string Body, HttpResponseMessage Message)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();

    public T As<T>() => JsonSerializer.Deserialize<T>(Body, Options) ?? throw new InvalidOperationException($"Empty body: {Body}");

    public Guid Id => Json.GetProperty("id").GetGuid();

    public string Version => Json.GetProperty("version").GetString()!;

    /// <summary>Asserts the status and returns this response (message includes the body on failure).</summary>
    public ApiResponse Expect(HttpStatusCode expected)
    {
        Assert.True(expected == Status, $"Expected {(int)expected} {expected} but got {(int)Status} {Status}. Body: {Body}");
        return this;
    }
}

/// <summary>
/// HTTP client with its own cookie jar and CSRF token. Unsafe requests automatically carry the antiforgery header.
/// Re-fetch the token after signing in or out (the token is bound to the signed-in user); <see cref="HelpdeskFixture"/> does this.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private string? _csrfHeader;
    private string? _csrfToken;

    public HttpClient Http { get; } = http;

    public async Task RefreshCsrfAsync()
    {
        var response = await GetAsync("/api/csrf");
        response.Expect(HttpStatusCode.OK);
        _csrfHeader = response.Json.GetProperty("headerName").GetString();
        _csrfToken = response.Json.GetProperty("token").GetString();
    }

    public Task<ApiResponse> GetAsync(string url) => SendAsync(HttpMethod.Get, url, null, includeCsrf: false);

    public Task<ApiResponse> PostAsync(string url, object? body = null) => SendAsync(HttpMethod.Post, url, body);

    public Task<ApiResponse> PutAsync(string url, object? body = null) => SendAsync(HttpMethod.Put, url, body);

    public Task<ApiResponse> PatchAsync(string url, object? body = null) => SendAsync(HttpMethod.Patch, url, body);

    public Task<ApiResponse> DeleteAsync(string url) => SendAsync(HttpMethod.Delete, url, null);

    public async Task<ApiResponse> SendAsync(HttpMethod method, string url, object? body, bool includeCsrf = true)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Options);
        }

        if (includeCsrf && _csrfHeader is not null && _csrfToken is not null)
        {
            request.Headers.Add(_csrfHeader, _csrfToken);
        }

        var message = await Http.SendAsync(request);
        return new ApiResponse(message.StatusCode, await message.Content.ReadAsStringAsync(), message);
    }
}

public sealed record TestUser(string Subject, Guid Id, ApiClient Client);

public sealed record DepartmentRef(Guid Id, string Key, string Name, string Version)
{
    public string Url => $"/api/departments/{Id}";
}
