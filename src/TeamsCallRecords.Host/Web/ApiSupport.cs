using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TeamsCallRecords.Host.Web;

/// <summary>Error format (TRP 3.11): {"error":{"code","message"},"trace_id"}.</summary>
public static class ApiError
{
    public sealed record Body(Detail Error, string TraceId);
    public sealed record Detail(string Code, string Message);

    public static IResult Result(HttpContext ctx, int status, string code, string message) =>
        Results.Json(new Body(new Detail(code, message), ctx.TraceIdentifier), statusCode: status);
}

public static class SecretCompare
{
    /// <summary>Constant-time comparison (both sides are hashed first, so the length does not leak).</summary>
    public static bool Equal(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var ha = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var hb = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }
}

/// <summary>Endpoint filter: requires header X-TCRI-Key equal to Api:Key.</summary>
public sealed class ApiKeyFilter : IEndpointFilter
{
    public const string Header = "X-TCRI-Key";
    private readonly IOptions<ApiOptions> _options;

    public ApiKeyFilter(IOptions<ApiOptions> options) => _options = options;

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var ctx = context.HttpContext;
        var given = ctx.Request.Headers[Header].ToString();
        if (given.Length == 0 || !SecretCompare.Equal(given, _options.Value.Key))
            return ValueTask.FromResult<object?>(ApiError.Result(ctx, StatusCodes.Status401Unauthorized, "unauthorized",
                "Missing or invalid " + Header));
        return next(context);
    }
}

public static class BodyReader
{
    /// <summary>Reads at most maxBytes; null means the body is larger.</summary>
    public static async Task<byte[]?> ReadLimitedAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength is { } len && len > maxBytes) return null;
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > maxBytes) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    public static bool IsJson(HttpRequest request) =>
        request.ContentType is { } ct && ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Adapts an options class's Validate() to IValidateOptions.</summary>
public sealed class OptionsValidator<T> : IValidateOptions<T> where T : class
{
    private readonly Func<T, IEnumerable<string>> _validate;
    public OptionsValidator(Func<T, IEnumerable<string>> validate) => _validate = validate;

    public ValidateOptionsResult Validate(string? name, T options)
    {
        var errors = _validate(options).ToList();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
