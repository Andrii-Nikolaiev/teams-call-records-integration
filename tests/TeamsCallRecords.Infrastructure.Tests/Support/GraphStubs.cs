using System.Net;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Infrastructure.Config;

namespace TeamsCallRecords.Infrastructure.Tests.Support;

/// <summary>What the stub saw: method, URI, Authorization header and body text.</summary>
public sealed record SeenRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

/// <summary>HTTP stub: a responder decides the answer for every request. No network.</summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<SeenRequest, int, CancellationToken, Task<HttpResponseMessage>> _responder;

    public StubHandler(Func<SeenRequest, int, HttpResponseMessage> responder)
        : this((r, n, _) => Task.FromResult(responder(r, n))) { }

    /// <summary>Asynchronous responder; it receives the request's cancellation token (timeouts are cooperative).</summary>
    public StubHandler(Func<SeenRequest, int, CancellationToken, Task<HttpResponseMessage>> responder) => _responder = responder;

    public List<SeenRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var seen = new SeenRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body);
        int number;
        lock (Requests)
        {
            Requests.Add(seen);
            number = Requests.Count;
        }
        return await _responder(seen, number, cancellationToken);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
}

public sealed class FakeCredential : TokenCredential
{
    public int Calls { get; private set; }
    public bool Fail { get; set; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Calls++;
        if (Fail) throw new AuthenticationFailedException("secret-detail-that-must-not-leak");
        return new ValueTask<AccessToken>(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
    }
}

public static class GraphTestOptions
{
    public static GraphOptions Valid() => new()
    {
        TenantId = "11111111-1111-4111-8111-111111111111",
        ClientId = "22222222-2222-4222-8222-222222222222",
        ClientSecret = "test-secret",
        MaxPages = 3,
        RetryMaxAttempts = 2,
        RetryBaseDelayMilliseconds = 1,
        RequestTimeoutSeconds = 1,
        TotalTimeoutSeconds = 20,
    };

    public static IOptions<GraphOptions> Wrap(GraphOptions o) => Microsoft.Extensions.Options.Options.Create(o);
}
