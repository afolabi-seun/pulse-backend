using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Organizations;
using Pulse.Domain.Teams;
using Pulse.Infrastructure.BackgroundJobs;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// Multi-tenancy Phase 1e: recurring jobs fan out one child per active organization, and each child runs
/// with that organization's data, thresholds and new-row stamping — without the job knowing about orgs.
/// </summary>
[Collection("Integration")]
public class OrganizationJobRunnerTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public OrganizationJobRunnerTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task A_job_run_for_an_organization_sees_only_that_organizations_data_and_thresholds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var orgB = await SeedOrganizationAsync($"jobs-b-{suffix}");
        var orgAEngineer = await SeedEngineerAsync($"jobs_a_{suffix}@pulse.io");
        var orgBEngineer = await SeedEngineerAsync($"jobs_b_{suffix}@pulse.io");
        await MoveToOrganizationAsync(orgBEngineer, orgB);
        await SeedThresholdAsync(orgB, "MaxConcurrentTasks", "11");

        var seen = await RunProbeForAsync(orgB, teamName: $"Job-created team {suffix}");

        seen.OrganizationId.Should().Be(orgB);
        seen.EngineerEmails.Should().Contain(orgBEngineer.Email).And.NotContain(orgAEngineer.Email);
        seen.MaxConcurrentTasks.Should().Be(11, "the job gets org B's thresholds, not the default org's");

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>(); // unscoped: sees every org
        (await db.Teams.SingleAsync(t => t.Name == $"Job-created team {suffix}")).OrganizationId
            .Should().Be(orgB, "rows a job creates belong to the organization it's running for");
    }

    [Fact]
    public async Task The_same_job_run_for_the_default_organization_does_not_see_another_organizations_data()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var orgB = await SeedOrganizationAsync($"jobs-d-{suffix}");
        var orgBEngineer = await SeedEngineerAsync($"jobs_d_{suffix}@pulse.io");
        await MoveToOrganizationAsync(orgBEngineer, orgB);

        var seen = await RunProbeForAsync(Organization.DefaultId);

        seen.EngineerEmails.Should().NotContain(orgBEngineer.Email);
    }

    [Fact]
    public async Task The_schedule_fans_out_one_child_per_active_organization()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var active = await SeedOrganizationAsync($"jobs-active-{suffix}");
        var inactive = await SeedOrganizationAsync($"jobs-inactive-{suffix}", isActive: false);

        var client = new RecordingJobClient();
        var runner = new OrganizationJobRunner<ProbeJob>(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(), client,
            NullLogger<OrganizationJobRunner<ProbeJob>>.Instance);

        await runner.RunForAllOrganizationsAsync(CancellationToken.None);

        client.Jobs.Should().OnlyContain(j => j.Type == typeof(OrganizationJobRunner<ProbeJob>)
            && j.Method.Name == nameof(OrganizationJobRunner<ProbeJob>.RunForOrganizationAsync));
        var enqueued = client.Jobs.Select(j => (Guid)j.Args[0]).ToList();
        enqueued.Should().Contain([Organization.DefaultId, active]).And.NotContain(inactive);
        enqueued.Should().OnlyHaveUniqueItems();
    }

    // ── Probe job ────────────────────────────────────────────────────────────

    public sealed record Seen(Guid? OrganizationId, IReadOnlyList<string> EngineerEmails, int MaxConcurrentTasks);

    /// <summary>A recurring job that records what it can see. Created by ActivatorUtilities, like any job
    /// the runner resolves without a DI registration. Static hand-off is fine: tests in one class run
    /// sequentially.</summary>
    public sealed class ProbeJob(ICurrentUserService currentUser, PulseDbContext db, OverworkThresholds thresholds) : IRecurringJob
    {
        internal static string? TeamNameToCreate;
        internal static Seen? LastSeen;

        public async Task RunAsync(CancellationToken ct = default)
        {
            var emails = await db.Engineers.Select(e => e.Email).ToListAsync(ct);
            if (TeamNameToCreate is string teamName)
            {
                db.Teams.Add(Team.Create(teamName));
                await db.SaveChangesAsync(ct);
            }
            LastSeen = new Seen(currentUser.OrganizationId, emails, thresholds.MaxConcurrentTasks);
        }
    }

    private async Task<Seen> RunProbeForAsync(Guid organizationId, string? teamName = null)
    {
        ProbeJob.TeamNameToCreate = teamName;
        ProbeJob.LastSeen = null;
        using var scope = Factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<OrganizationJobRunner<ProbeJob>>();

        await runner.RunForOrganizationAsync(organizationId, CancellationToken.None);

        return ProbeJob.LastSeen ?? throw new InvalidOperationException("The probe job didn't run.");
    }

    /// <summary>Captures enqueued jobs instead of storing them.</summary>
    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<Job> Jobs { get; } = [];

        public string Create(Job job, IState state)
        {
            Jobs.Add(job);
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private async Task<Guid> SeedOrganizationAsync(string slug, bool isActive = true)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var org = Organization.Create("Jobs org", slug);
        if (!isActive) org.Deactivate();
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }

    private async Task SeedThresholdAsync(Guid organizationId, string key, string value)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.ThresholdSettings.Add(new ThresholdSetting { OrganizationId = organizationId, Key = key, Value = value });
        await db.SaveChangesAsync();
    }

    private async Task MoveToOrganizationAsync(Engineer engineer, Guid organizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Attach(engineer);
        db.Entry(engineer).Property(e => e.OrganizationId).CurrentValue = organizationId;
        await db.SaveChangesAsync();
    }
}
