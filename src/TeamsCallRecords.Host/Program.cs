using TeamsCallRecords.Host;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TeamsCallRecords.Host.Demo;
using TeamsCallRecords.Host.Web;
using TeamsCallRecords.Host.Workers;
using TeamsCallRecords.Infrastructure;
using TeamsCallRecords.Infrastructure.Graph;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTcriInfrastructure(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
AddValidated<ApiOptions>(builder, "Api", o => o.Validate());
AddValidated<WebhookOptions>(builder, "Webhook", o => o.Validate());
builder.Services.AddOptions<DemoOptions>().Bind(builder.Configuration.GetSection("Demo"));
builder.Services.AddSingleton<ReadinessState>();

// Graph:Mode selects the IGraphClient. Demo (default): an in-memory stand-in, no Microsoft 365 tenant needed.
// Live: the real Microsoft Graph client; the demo endpoints (which need the in-memory client) must be off.
var graphMode = builder.Configuration["Graph:Mode"] ?? "Demo";
if (graphMode.Equals("Live", StringComparison.OrdinalIgnoreCase))
{
    if (builder.Configuration.GetValue<bool>("Demo:Enabled"))
        throw new InvalidOperationException("Graph:Mode=Live requires Demo:Enabled=false (set Demo__Enabled=false)");
    builder.Services.AddTcriGraphClient();
}
else if (graphMode.Equals("Demo", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<InMemoryGraphClient>();
    builder.Services.AddSingleton<IGraphClient>(sp => sp.GetRequiredService<InMemoryGraphClient>());
}
else
{
    throw new InvalidOperationException($"Graph:Mode must be Demo or Live, got '{graphMode}'");
}

builder.Services.AddHostedService<InboxWorker>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<ReconcileWorker>();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var result = ApiError.Result(ctx, StatusCodes.Status500InternalServerError, "internal_error", "Unexpected error");
    await result.ExecuteAsync(ctx);
}));

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (ReadinessState readiness) =>
    readiness.IsReady ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapGraphWebhook();
app.MapTcriApi();
if (app.Services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled) app.MapDemo();
app.MapFallback(ctx => ApiError.Result(ctx, StatusCodes.Status404NotFound, "not_found", "No such endpoint").ExecuteAsync(ctx));

await app.Services.MigrateAsync(app.Lifetime.ApplicationStopping);
app.Services.GetRequiredService<ReadinessState>().SetReady();

await app.RunAsync();

static void AddValidated<T>(WebApplicationBuilder b, string section, Func<T, IEnumerable<string>> validate) where T : class
{
    b.Services.AddOptions<T>().Bind(b.Configuration.GetSection(section)).ValidateOnStart();
    b.Services.AddSingleton<IValidateOptions<T>>(new OptionsValidator<T>(validate));
}

public partial class Program;
