using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pulse.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by EF Core tooling (dotnet ef migrations).
/// Not used at runtime — the real DbContext is configured via DI in Program.cs.
/// </summary>
public class PulseDbContextFactory : IDesignTimeDbContextFactory<PulseDbContext>
{
    public PulseDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PulseDbContext>()
            .UseNpgsql("Host=localhost;Database=pulse_dev;Username=pulse;Password=devpassword")
            .Options;

        return new PulseDbContext(options);
    }
}
