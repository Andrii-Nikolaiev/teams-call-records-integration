using System.Net;
using Polly.CircuitBreaker;
using Polly.Timeout;
using TeamsCallRecords.Infrastructure.Config;
using TeamsCallRecords.Infrastructure.Graph;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

/// <summary>Retry, timeout and circuit breaker policies (TRP 3.14.4) against an HTTP stub. No network.</summary>
public class GraphResilienceTests
{
    private const string Url = "https://graph.microsoft.com/v1.0/x";

    private static HttpClient Build(StubHandler stub, Action<GraphOptions>? tune = null)
    {
        var options = GraphTestOptions.Valid();
        tune?.Invoke(options);
        var pipeline = new GraphResiliencePipeline(GraphTestOptions.Wrap(options), TimeProvider.System);
        return new HttpClient(new GraphResilienceHandler(pipeline) { InnerHandler = stub }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static StubHandler Stub(Func<SeenRequest, int, HttpResponseMessage> responder) => new(responder);

    private static Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method) =>
        http.SendAsync(new HttpRequestMessage(method, Url) { Content = method == HttpMethod.Get ? null : new StringContent("{}") });

    [Fact]
    public async Task Get_RetriesServerErrorsUntilSuccess()
    {
        var stub = Stub((_, n) => StubHandler.Status(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        var http = Build(stub);

        using var response = await Send(http, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task Get_GivesUpAfterRetryMaxAttempts_ReturnsLastStatus()
    {
        var stub = Stub((_, _) => StubHandler.Status(HttpStatusCode.BadGateway));
        var http = Build(stub);

        using var response = await Send(http, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(3, stub.Requests.Count);   // 1 + RetryMaxAttempts(2)
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    public async Task Get_Retries408And429(int code)
    {
        var stub = Stub((_, n) => StubHandler.Status(n == 1 ? (HttpStatusCode)code : HttpStatusCode.OK));
        var http = Build(stub);

        using var response = await Send(http, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task Get_DoesNotRetryClientErrors(int code)
    {
        var stub = Stub((_, _) => StubHandler.Status((HttpStatusCode)code));
        var http = Build(stub);

        using var response = await Send(http, HttpMethod.Get);

        Assert.Equal(code, (int)response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Patch_And_Delete_AreRetriedLikeGet()
    {
        foreach (var method in new[] { HttpMethod.Patch, HttpMethod.Delete })
        {
            var stub = Stub((_, n) => StubHandler.Status(n == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
            var http = Build(stub);

            using var response = await Send(http, method);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, stub.Requests.Count);
        }
    }

    [Fact]
    public async Task Post_IsNotRetriedOn5xx()
    {
        var stub = Stub((_, _) => StubHandler.Status(HttpStatusCode.ServiceUnavailable));
        var http = Build(stub);

        using var response = await Send(http, HttpMethod.Post);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Post_IsRetriedOn429Only()
    {
        var stub = Stub((_, n) => StubHandler.Status(n == 1 ? (HttpStatusCode)429 : HttpStatusCode.Created));
        var http = Build(stub);

        using var response = await Send(http, HttpMethod.Post);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Get_RetriesNetworkErrors()
    {
        var stub = Stub((_, n) => n == 1 ? throw new HttpRequestException("boom") : StubHandler.Status(HttpStatusCode.OK));
        var http = Build(stub);

        using var response = await Send(http, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Post_IsNotRetriedOnNetworkError()
    {
        var stub = Stub((_, _) => throw new HttpRequestException("boom"));
        var http = Build(stub);

        await Assert.ThrowsAsync<HttpRequestException>(() => Send(http, HttpMethod.Post));

        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task RetryAfterHeader_IsAccepted()
    {
        var stub = Stub((_, n) =>
        {
            if (n > 1) return StubHandler.Status(HttpStatusCode.OK);
            var response = StubHandler.Status((HttpStatusCode)429);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        var http = Build(stub);

        using var response = await Send(http, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task RetryMaxAttemptsZero_MeansNoRetries()
    {
        var stub = Stub((_, _) => StubHandler.Status(HttpStatusCode.ServiceUnavailable));
        var http = Build(stub, o => o.RetryMaxAttempts = 0);

        using var response = await Send(http, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task AttemptTimeout_Post_IsNotRetried()
    {
        var hang = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return StubHandler.Status(HttpStatusCode.OK);
        });
        var http = Build(hang);   // RequestTimeoutSeconds = 1

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => Send(http, HttpMethod.Post));

        Assert.Single(hang.Requests);
    }

    [Fact]
    public async Task AttemptTimeout_Get_IsRetried()
    {
        var hang = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return StubHandler.Status(HttpStatusCode.OK);
        });
        var http = Build(hang, o => o.RetryMaxAttempts = 1);

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => Send(http, HttpMethod.Get));

        Assert.Equal(2, hang.Requests.Count);
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterFiveFailures_AndStopsSending()
    {
        var stub = Stub((_, _) => StubHandler.Status(HttpStatusCode.InternalServerError));
        var http = Build(stub, o => o.RetryMaxAttempts = 0);

        for (var i = 0; i < 5; i++)
        {
            using var response = await Send(http, HttpMethod.Get);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(() => Send(http, HttpMethod.Get));
        Assert.Equal(5, stub.Requests.Count);   // the sixth request was not sent
    }
}
