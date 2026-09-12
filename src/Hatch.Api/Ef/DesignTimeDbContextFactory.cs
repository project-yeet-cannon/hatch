using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hatch.Api.Ef;

/// <summary>
/// Lets the EF Core CLI (`dotnet ef migrations …`) build an AppDbContext
/// without executing Program.cs, which otherwise reads .env.json and reaches
/// out to Home Assistant / Quartz at startup. Only the model shape is needed
/// to scaffold migrations, so the connection string here is nominal.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=hatch;Username=user;Password=password")
            .Options;
        return new AppDbContext(options);
    }
}
