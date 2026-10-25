using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using TeamsCallRecords.Infrastructure.Config;

namespace TeamsCallRecords.Infrastructure.Graph;

/// <summary>
/// Resilience policies for Graph requests (TRP 3.14.4): total timeout, retry, attempt timeout, circuit breaker.
/// One instance per process, so the circuit breaker state survives HttpClient handler rotation.
/// </summary>
public sealed class GraphResiliencePipeline
{
    internal static readonly ResiliencePropertyKey<HttpMethod> MethodKey = new("tcri.http-method");

    private readonly TimeProvider _clock;

    public GraphResiliencePipeline(IOptions<GraphOptions> options, TimeProvider clock)
    {
        _clock = clock;
        var o = options.Value;
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = clock };

        builder.AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(o.TotalTimeoutSeconds) });

        // RetryStrategyOptions does not accept 0 attempts: "no retries" means "no retry strategy".
        if (o.RetryMaxAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = o.RetryMaxAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(o.RetryBaseDelayMilliseconds),
                ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome, args.Context)),
                DelayGenerator = args => ValueTask.FromResult(RetryAfter(args.Outcome.Result)),
                OnRetry = args =>
                {
                    args.Outcome.Result?.Dispose();
                    return default;
                },
            });
        }

        builder.AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(o.RequestTimeoutSeconds) });

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            MinimumThroughput = 5,
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(60),
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = args => ValueTask.FromResult(IsFailure(args.Outcome)),
        });

        Pipeline = builder.Build();
    }

    internal ResiliencePipeline<HttpResponseMessage> Pipeline { get; }

    private static bool IsFailure(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is HttpRequestException or TimeoutRejectedException) return true;
        if (outcome.Result is { } r)
        {
            var code = (int)r.StatusCode;
            return code == 408 || code == 429 || code >= 500;
        }
        return false;
    }

    /// <summary>
    /// GET, PATCH, DELETE: retry 408, 429, 5xx and network faults. POST: only 429 (the server refused without
    /// executing the request); a POST after timeout, network fault or 5xx may already have been executed.
    /// </summary>
    private static bool ShouldRetry(Outcome<HttpResponseMessage> outcome, ResilienceContext context)
    {
        var isPost = context.Properties.TryGetValue(MethodKey, out var method) && method == HttpMethod.Post;

        if (outcome.Exception is HttpRequestException or TimeoutRejectedException) return !isPost;

        if (outcome.Result is { } r)
        {
            var code = (int)r.StatusCode;
            if (code == 429) return true;
            if (code == 408 || code >= 500) return !isPost;
        }
        return false;
    }

    private TimeSpan? RetryAfter(HttpResponseMessage? response)
    {
        var header = response?.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header.Date is { } date)
        {
            var wait = date - _clock.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }
}

/// <summary>Runs every request through <see cref="GraphResiliencePipeline"/>.</summary>
public sealed class GraphResilienceHandler : DelegatingHandler
{
    private readonly GraphResiliencePipeline _pipeline;

    public GraphResilienceHandler(GraphResiliencePipeline pipeline) => _pipeline = pipeline;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(GraphResiliencePipeline.MethodKey, request.Method);
        try
        {
            return await _pipeline.Pipeline.ExecuteAsync(
                async ctx => await base.SendAsync(request, ctx.CancellationToken).ConfigureAwait(false),
                context).ConfigureAwait(false);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
