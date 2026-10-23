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

// Demo mode: an in-memory stand-in for Microsoft Graph (the real client needs a Microsoft 365 tenant).
builder.Services.AddSingleton<InMemoryGraphClient>();
builder.Services.AddSingleton<IGraphClient>(sp => sp.GetRequiredService<InMemoryGraphClient>());

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
