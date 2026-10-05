using Pulse.Domain.CheckIns;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.CheckIns;

/// <summary>The BackfillHandoffCheckIns data fix replays task history into the daily check-in —
/// see <see cref="HandoffCheckInBackfill"/>.</summary>
[Collection("Integration")]
public class HandoffCheckInBackfillTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public HandoffCheckInBackfillTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task<List<CheckIn>> CheckInsAsync(Guid engineerId, Guid projectId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        return await db.CheckIns.AsNoTracking()
            .Where(c => c.EngineerId == engineerId && c.ProjectId == projectId).ToListAsync();
    }

    private async Task RunBackfillAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        await db.Database.ExecuteSqlRawAsync(HandoffCheckInBackfill.Sql);
    }

    [Fact]
    public async Task Past_handoffs_and_sends_to_QA_are_added_to_the_right_engineers_check_in()
    {
        var backend = await SeedEngineerAsync($"bf_be_{Guid.NewGuid():N}@pulse.io");
        var frontend = await SeedEngineerAsync($"bf_fe_{Guid.NewGuid():N}@pulse.io");
        var builder = await SeedEngineerAsync($"bf_qa_{Guid.NewGuid():N}@pulse.io");
        var pmo = await SeedEngineerAsync($"bf_pmo_{Guid.NewGuid():N}@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Backfill project");
        var twoStage = await SeedTaskAsync("Profile page", project.Id, assigneeId: backend.Id);
        var needsQa = await SeedTaskAsync("Checkout flow", project.Id, assigneeId: builder.Id, requiresQa: true);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var a = await db.Tasks.FirstAsync(t => t.Id == twoStage.Id);
            a.SetRequiresFrontendHandoff(true);
            // Performed by PMO on the backend engineer's behalf.
            a.HandOffToFrontend(frontend.Id, pmo.Id);
            var b = await db.Tasks.FirstAsync(t => t.Id == needsQa.Id);
            b.SendToQa(pmo.Id);
            await db.SaveChangesAsync();
        }

        (await CheckInsAsync(backend.Id, project.Id)).Should().BeEmpty("nothing recorded these before");

        await RunBackfillAsync();

        var backendCheckIn = (await CheckInsAsync(backend.Id, project.Id)).Should().ContainSingle().Subject;
        backendCheckIn.Completed.Should().Be("Handed off to Frontend: Profile page",
            "the entry belongs to the engineer the task was handed FROM, not the PMO who clicked");
        backendCheckIn.Date.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));

        (await CheckInsAsync(builder.Id, project.Id)).Should().ContainSingle()
            .Which.Completed.Should().Be("Sent to QA: Checkout flow");
        (await CheckInsAsync(frontend.Id, project.Id)).Should().BeEmpty("the receiving engineer did nothing yet");
        (await CheckInsAsync(pmo.Id, project.Id)).Should().BeEmpty("the actor is not credited when the assignee is known");
    }

    [Fact]
    public async Task Entries_are_appended_to_an_existing_check_in_and_the_backfill_is_idempotent()
    {
        var backend = await SeedEngineerAsync($"bf2_be_{Guid.NewGuid():N}@pulse.io");
        var frontend = await SeedEngineerAsync($"bf2_fe_{Guid.NewGuid():N}@pulse.io");
        var project = await SeedProjectAsync("Backfill idempotent project");
        var task = await SeedTaskAsync("Settings page", project.Id, assigneeId: backend.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var tracked = await db.Tasks.FirstAsync(t => t.Id == task.Id);
            tracked.SetRequiresFrontendHandoff(true);
            tracked.HandOffToFrontend(frontend.Id, backend.Id);
            // The engineer had already written their own check-in that day.
            db.CheckIns.Add(CheckIn.Submit(backend.Id, DateOnly.FromDateTime(DateTime.UtcNow),
                "Reviewed the API contract", "Pairing", null, project.Id));
            await db.SaveChangesAsync();
        }

        await RunBackfillAsync();

        var after = (await CheckInsAsync(backend.Id, project.Id)).Should().ContainSingle().Subject;
        after.Completed.Should().Be("Reviewed the API contract\nHanded off to Frontend: Settings page");
        after.PlannedNext.Should().Be("Pairing", "the engineer's own planning text is untouched");

        await RunBackfillAsync();
        (await CheckInsAsync(backend.Id, project.Id)).Single().Completed.Should().Be(after.Completed, "re-running changes nothing");
    }

    [Fact]
    public async Task An_entry_already_written_live_is_not_duplicated()
    {
        var backend = await SeedEngineerAsync($"bf3_be_{Guid.NewGuid():N}@pulse.io");
        var frontend = await SeedEngineerAsync($"bf3_fe_{Guid.NewGuid():N}@pulse.io");
        var project = await SeedProjectAsync("Backfill live project");
        var task = await SeedTaskAsync("Search page", project.Id, assigneeId: backend.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var tracked = await db.Tasks.FirstAsync(t => t.Id == task.Id);
            tracked.SetRequiresFrontendHandoff(true);
            tracked.HandOffToFrontend(frontend.Id, backend.Id);
            db.CheckIns.Add(CheckIn.Submit(backend.Id, DateOnly.FromDateTime(DateTime.UtcNow),
                "Handed off to Frontend: Search page", string.Empty, null, project.Id));
            await db.SaveChangesAsync();
        }

        await RunBackfillAsync();

        (await CheckInsAsync(backend.Id, project.Id)).Single().Completed.Should().Be("Handed off to Frontend: Search page");
    }
}
