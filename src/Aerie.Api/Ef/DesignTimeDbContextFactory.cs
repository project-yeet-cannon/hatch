using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Aerie.Api.Ef;

/// <summary>
/// Lets the EF Core CLI (`dotnet ef migrations …`) build an AerieContext
/// without executing Program.cs, which otherwise reads .env.json and reaches
/// out to Home Assistant / Quartz at startup. Only the model shape is needed
/// to scaffold migrations, so the connection string here is nominal.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AerieContext>
{
    public AerieContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AerieContext>()
            .UseNpgsql("Host=localhost;Database=aerie;Username=user;Password=password")
            .Options;
        return new AerieContext(options);
    }
}
