using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TeamsCallRecords.Host.Demo;

namespace TeamsCallRecords.Host.Tests;

/// <summary>Starts the real Host with a temporary database and test secrets.</summary>
public sealed class TcriFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-api-key-0123456789";
    public const string ClientState = "test-client-state-0001";
    public const string Tenant = "11111111-1111-4111-8111-111111111111";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcri-host-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:DataDirectory", _dir);
        builder.UseSetting("Api:Key", ApiKey);
        builder.UseSetting("Webhook:ClientState", ClientState);
        builder.UseSetting("Demo:Enabled", "true");
        builder.UseSetting("Graph:TenantId", Tenant);
    }

    public InMemoryGraphClient Graph => Services.GetRequiredService<InMemoryGraphClient>();

    public HttpClient ApiClient()
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-TCRI-Key", ApiKey);
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch (IOException) { /* temp files; the OS cleans them later */ }
        catch (UnauthorizedAccessException) { }
    }
}
