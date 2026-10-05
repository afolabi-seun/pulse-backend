namespace Pulse.Application.Common.Interfaces;

public interface IDemoSeeder
{
    Task<DemoSeedSummary> SeedAsync(CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public record DemoSeedSummary(
    int Engineers,
    int Teams,
    int Projects,
    int Epics,
    int Sprints,
    int Tasks,
    int CheckIns,
    int VitalsResponses,
    int Feedback,
    int Notifications,
    int Members,
    int WikiPages,
    string DefaultPassword
);
