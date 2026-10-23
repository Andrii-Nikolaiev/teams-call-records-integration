using TeamsCallRecords.Host;
using TeamsCallRecords.Host.Demo;
using TeamsCallRecords.Host.Workers;
using TeamsCallRecords.Infrastructure;
using TeamsCallRecords.Infrastructure.Graph;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTcriInfrastructure(builder.Configuration);
builder.Services.AddSingleton<ReadinessState>();

// Demo mode: an in-memory stand-in for Microsoft Graph (the real client needs a Microsoft 365 tenant).
builder.Services.AddSingleton<InMemoryGraphClient>();
builder.Services.AddSingleton<IGraphClient>(sp => sp.GetRequiredService<InMemoryGraphClient>());

builder.Services.AddHostedService<InboxWorker>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<ReconcileWorker>();

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (ReadinessState readiness) =>
    readiness.IsReady ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

await app.Services.MigrateAsync(app.Lifetime.ApplicationStopping);
app.Services.GetRequiredService<ReadinessState>().SetReady();

await app.RunAsync();

public partial class Program;
