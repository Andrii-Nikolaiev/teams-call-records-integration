using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TeamsCallRecords.Host.Web;

namespace TeamsCallRecords.Host.Tests;

/// <summary>GET /api/status reports the mode the Host was registered with (not a fixed value).</summary>
public sealed class StatusModeTests
{
    [Fact]
    public async Task Status_reports_demo_by_default()
    {
        using var f = new TcriFactory();
        Assert.Equal("demo", await ReadModeAsync(f.ApiClient()));
    }

    [Fact]
    public async Task Status_reports_live_when_the_host_is_registered_in_live_mode()
    {
        using var f = new TcriFactory();
        using var live = f.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton(new HostMode(HostMode.Live))));
        var c = live.CreateClient();
        c.DefaultRequestHeaders.Add("X-TCRI-Key", TcriFactory.ApiKey);
        Assert.Equal("live", await ReadModeAsync(c));
    }

    private static async Task<string?> ReadModeAsync(HttpClient c)
    {
        var r = await c.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("mode").GetString();
    }
}
