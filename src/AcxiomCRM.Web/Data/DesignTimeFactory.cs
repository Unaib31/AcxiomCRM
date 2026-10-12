using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AcxiomCRM.Data;

/// <summary>Lets `dotnet ef` create the context without booting the web host (and its seeding).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=design-time.db").Options;
        return new AppDbContext(options);
    }
}
