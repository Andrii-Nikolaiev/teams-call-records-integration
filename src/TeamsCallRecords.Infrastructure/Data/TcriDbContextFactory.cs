using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TeamsCallRecords.Infrastructure.Data;

/// <summary>Design-time factory for <c>dotnet ef</c>; no runtime configuration.</summary>
public sealed class TcriDbContextFactory : IDesignTimeDbContextFactory<TcriDbContext>
{
    public TcriDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TcriDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new TcriDbContext(options);
    }
}
