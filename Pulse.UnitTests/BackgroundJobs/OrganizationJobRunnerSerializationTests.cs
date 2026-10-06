using Hangfire.Common;
using Hangfire.Storage;
using Pulse.Application.Escalations;
using Pulse.Application.Overwork;
using Pulse.Infrastructure.BackgroundJobs;
using FluentAssertions;

namespace Pulse.UnitTests.BackgroundJobs;

/// <summary>
/// OrganizationJobRunner is generic. Hangfire stores a job as serialized invocation data and
/// rebuilds it on the worker — if the job type didn't survive that round trip, every recurring job
/// would fail at run time, not at registration. These pin the round trip down.
/// </summary>
public class OrganizationJobRunnerSerializationTests
{
    [Fact]
    public void The_fan_out_job_round_trips_with_its_job_type()
    {
        var job = Job.FromExpression<OrganizationJobRunner<EscalationScanner>>(r => r.RunForAllOrganizationsAsync(CancellationToken.None));

        var restored = InvocationData.SerializeJob(job).DeserializeJob();

        restored.Type.Should().Be(typeof(OrganizationJobRunner<EscalationScanner>));
        restored.Method.Name.Should().Be(nameof(OrganizationJobRunner<EscalationScanner>.RunForAllOrganizationsAsync));
    }

    [Fact]
    public void The_per_organization_job_round_trips_with_its_job_type_and_organization()
    {
        var orgId = Guid.NewGuid();
        var job = Job.FromExpression<OrganizationJobRunner<OverworkDigestJob>>(r => r.RunForOrganizationAsync(orgId, CancellationToken.None));

        var restored = InvocationData.SerializeJob(job).DeserializeJob();

        restored.Type.Should().Be(typeof(OrganizationJobRunner<OverworkDigestJob>));
        restored.Args[0].Should().Be(orgId);
    }
}
