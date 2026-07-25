using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CogniLens.Infrastructure.Persistence;

// Used only by `dotnet ef migrations add` at design time. Runtime DI configures the
// context via Program.cs in the Api/Worker hosts instead.
public class CogniLensDbContextFactory : IDesignTimeDbContextFactory<CogniLensDbContext>
{
    public CogniLensDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("COGNILENS_DB_CONNECTION")
            ?? "Server=localhost,1433;Database=CogniLens;User Id=sa;Password=DevPassword1!;TrustServerCertificate=True";

        var optionsBuilder = new DbContextOptionsBuilder<CogniLensDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        return new CogniLensDbContext(optionsBuilder.Options);
    }
}
