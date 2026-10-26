using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace TeamsCallRecords.Host.Tests;

/// <summary>
/// /api/messages is authenticated by the Teams SDK (TRP 3.24). These tests need no network: a request without a token,
/// or with a token that is not a JWT, is rejected before any key download. Tests with signed tokens (bad signature,
/// wrong audience, expired, valid) need a local signing key and an OpenID metadata stand-in and are not written yet.
/// </summary>
public sealed class BotAuthenticationTests : IClassFixture<TcriFactory>
{
    private const string ActivityJson =
        """{"type":"message","id":"1","text":"help","channelId":"msteams","serviceUrl":"http://localhost:9/","from":{"id":"u1"},"recipient":{"id":"bot"},"conversation":{"id":"c1"}}""";

    private readonly TcriFactory _factory;

    public BotAuthenticationTests(TcriFactory factory) => _factory = factory;

    private HttpClient ConfiguredClient() => _factory.WithWebHostBuilder(b =>
    {
        // Fake values: authentication is configured, so the SDK validates tokens (no bypass).
        b.UseSetting("AzureAd:TenantId", "33333333-3333-4333-8333-333333333333");
        b.UseSetting("AzureAd:ClientId", "44444444-4444-4444-8444-444444444444");
        b.UseSetting("AzureAd:ClientCredentials:0:SourceType", "ClientSecret");
        b.UseSetting("AzureAd:ClientCredentials:0:ClientSecret", "test-secret-not-real");
    }).CreateClient();

    private static StringContent Body() => new(ActivityJson, Encoding.UTF8, "application/json");

    [Fact]
    public async Task NotConfigured401()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsync("/api/messages", Body());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingBearer401()
    {
        using var client = ConfiguredClient();
        using var response = await client.PostAsync("/api/messages", Body());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedBearer401()
    {
        using var client = ConfiguredClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        using var response = await client.PostAsync("/api/messages", Body());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
