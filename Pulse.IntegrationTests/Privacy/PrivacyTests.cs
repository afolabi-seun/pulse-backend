using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Feedback;
using Pulse.Application.Overrides;
using Pulse.Application.Vitals;
using Pulse.Domain.Engineers;
using Pulse.Domain.Feedback;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Privacy;

[Collection("Integration")]
public class PrivacyTests : IClassFixture<PulseWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly PulseWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public PrivacyTests(PulseWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<Engineer> SeedEngineerAsync(string email, string role = Roles.Engineer)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<Application.Common.Interfaces.IPasswordHasher>();
        var engineer = Engineer.Create("Test User", email, hasher.Hash("Str0ng!Pass12"), role, 20, 14);
        db.Engineers.Add(engineer);
        await db.SaveChangesAsync();
        return engineer;
    }

    private async Task<string> LoginTokenAsync(string email)
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Str0ng!Pass12" });
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<AuthDto>>(JsonOpts);
        return body!.Data!.AccessToken;
    }

    private HttpClient AuthenticatedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ── feedback access control ───────────────────────────────────────────────

    [Fact]
    public async Task Engineer_can_submit_feedback()
    {
        await SeedEngineerAsync("fb_submit@vitals.io");
        var token = await LoginTokenAsync("fb_submit@vitals.io");
        var client = AuthenticatedClient(token);

        var response = await client.PostAsJsonAsync("/api/v1/feedback", new { text = "Great week!" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Engineer_cannot_read_feedback_list()
    {
        await SeedEngineerAsync("fb_read_denied@vitals.io");
        var token = await LoginTokenAsync("fb_read_denied@vitals.io");
        var client = AuthenticatedClient(token);

        var response = await client.GetAsync("/api/v1/feedback");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Engineer_cannot_read_feedback_patterns()
    {
        await SeedEngineerAsync("fb_pattern_denied@vitals.io");
        var token = await LoginTokenAsync("fb_pattern_denied@vitals.io");
        var client = AuthenticatedClient(token);

        var response = await client.GetAsync("/api/v1/feedback/patterns");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Head_can_read_feedback_and_audit_entry_is_created()
    {
        var head = await SeedEngineerAsync("fb_head_read@vitals.io", Roles.HeadOfRnD);
        var engineer = await SeedEngineerAsync("fb_audit_eng@vitals.io");

        var engToken = await LoginTokenAsync("fb_audit_eng@vitals.io");
        await AuthenticatedClient(engToken).PostAsJsonAsync("/api/v1/feedback", new { text = "Audit test feedback" });

        var headToken = await LoginTokenAsync("fb_head_read@vitals.io");
        var headClient = AuthenticatedClient(headToken);

        var response = await headClient.GetAsync("/api/v1/feedback");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify audit log entry was created
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var auditEntry = db.AuditLog.FirstOrDefault(a => a.ActorId == head.Id && a.Action == "FEEDBACK_READ");
        auditEntry.Should().NotBeNull("reading feedback must create an audit log entry");
    }

    [Fact]
    public async Task Hr_can_read_feedback_list_and_patterns()
    {
        var engineer = await SeedEngineerAsync("fb_hr_read_eng@vitals.io");
        await SeedEngineerAsync("fb_hr_read_hr@vitals.io", Roles.HR);

        var engToken = await LoginTokenAsync("fb_hr_read_eng@vitals.io");
        await AuthenticatedClient(engToken).PostAsJsonAsync("/api/v1/feedback", new { text = "Feedback for HR to read" });

        var hrToken = await LoginTokenAsync("fb_hr_read_hr@vitals.io");
        var hrClient = AuthenticatedClient(hrToken);

        var listResponse = await hrClient.GetAsync("/api/v1/feedback");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listBody = await listResponse.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<FeedbackDto>>>(JsonOpts);
        listBody!.Data!.Should().Contain(f => f.EngineerId == engineer.Id);

        var patternsResponse = await hrClient.GetAsync("/api/v1/feedback/patterns");
        patternsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hr_sees_feedback_from_every_department_not_just_one()
    {
        // The pre-existing department-scoping branch (used by a plain department head) must not
        // accidentally apply to HR — HR is org-wide by design, same as HeadOfPmo/ProjectManager.
        Guid teamAId, teamBId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var teamA = Domain.Teams.Team.Create("Dept A team", department: "Department A");
            var teamB = Domain.Teams.Team.Create("Dept B team", department: "Department B");
            db.Teams.AddRange(teamA, teamB);
            await db.SaveChangesAsync();
            teamAId = teamA.Id;
            teamBId = teamB.Id;
        }

        var engA = await SeedEngineerAsync("fb_hr_deptA@vitals.io");
        var engB = await SeedEngineerAsync("fb_hr_deptB@vitals.io");
        await SeedEngineerAsync("fb_hr_multidept@vitals.io", Roles.HR);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var a = await db.Engineers.FindAsync(engA.Id);
            a!.AssignToTeam(teamAId);
            var b = await db.Engineers.FindAsync(engB.Id);
            b!.AssignToTeam(teamBId);
            await db.SaveChangesAsync();
        }

        await AuthenticatedClient(await LoginTokenAsync("fb_hr_deptA@vitals.io"))
            .PostAsJsonAsync("/api/v1/feedback", new { text = "From department A" });
        await AuthenticatedClient(await LoginTokenAsync("fb_hr_deptB@vitals.io"))
            .PostAsJsonAsync("/api/v1/feedback", new { text = "From department B" });

        var hrClient = AuthenticatedClient(await LoginTokenAsync("fb_hr_multidept@vitals.io"));
        var response = await hrClient.GetAsync("/api/v1/feedback");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<FeedbackDto>>>(JsonOpts);
        body!.Data!.Should().Contain(f => f.EngineerId == engA.Id).And.Contain(f => f.EngineerId == engB.Id);
    }

    // ── feedback patterns minimum ─────────────────────────────────────────────

    [Fact]
    public async Task Patterns_excludes_weeks_with_fewer_than_3_distinct_sources()
    {
        // Use a past week (52 weeks ago) to avoid collisions with the current-week data
        // submitted by Patterns_includes_weeks_with_3_or_more_distinct_sources, which runs
        // in the same class and same DB.
        var pastWeek = new DateOnly(2025, 1, 6); // A Monday in Jan 2025 — always in the past

        var head = await SeedEngineerAsync("fb_excl_head@vitals.io", Roles.HeadOfRnD);
        var eng1 = await SeedEngineerAsync("fb_excl_eng1@vitals.io");
        var eng2 = await SeedEngineerAsync("fb_excl_eng2@vitals.io");

        // Directly seed only 2 feedback rows in the DB for the past week (below the 3-source threshold)
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.Feedback.Add(Feedback.Submit(eng1.Id, "Source one", pastWeek));
            db.Feedback.Add(Feedback.Submit(eng2.Id, "Source two", pastWeek));
            await db.SaveChangesAsync();
        }

        var headToken = await LoginTokenAsync("fb_excl_head@vitals.io");
        var response = await AuthenticatedClient(headToken).GetAsync("/api/v1/feedback/patterns");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<FeedbackPatternsDto>>(JsonOpts);

        body!.Data!.Weeks.Should().NotContain(p => p.WeekOf == pastWeek,
            "weeks with fewer than 3 distinct sources must be excluded from patterns");
        body.Data.HiddenWeeks.Should().BeGreaterThan(0, "excluded weeks are counted so their absence is explained");
    }

    [Fact]
    public async Task Patterns_includes_weeks_with_3_or_more_distinct_sources()
    {
        // Seed a head and three engineers
        await SeedEngineerAsync("fb_pat3_head@vitals.io", Roles.HeadOfRnD);
        await SeedEngineerAsync("fb_pat3_eng1@vitals.io");
        await SeedEngineerAsync("fb_pat3_eng2@vitals.io");
        await SeedEngineerAsync("fb_pat3_eng3@vitals.io");

        foreach (var email in new[] { "fb_pat3_eng1@vitals.io", "fb_pat3_eng2@vitals.io", "fb_pat3_eng3@vitals.io" })
        {
            var t = await LoginTokenAsync(email);
            await AuthenticatedClient(t).PostAsJsonAsync("/api/v1/feedback", new { text = $"Feedback from {email}" });
        }

        var headToken = await LoginTokenAsync("fb_pat3_head@vitals.io");
        var response = await AuthenticatedClient(headToken).GetAsync("/api/v1/feedback/patterns");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<FeedbackPatternsDto>>(JsonOpts);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dayOfWeek = (int)today.DayOfWeek;
        var weekOf = today.AddDays(dayOfWeek == 0 ? -6 : 1 - dayOfWeek);

        body!.Data!.Weeks.Should().Contain(p => p.WeekOf == weekOf && p.DistinctSources >= 3,
            "weeks with 3+ distinct sources must appear in patterns");
    }

    [Fact]
    public async Task Department_head_sees_patterns_for_their_own_department_only_and_hr_sees_all()
    {
        var week = new DateOnly(2025, 3, 3); // a Monday, long past
        var rdEngineers = new List<Guid>(); var otherEngineers = new List<Guid>();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var rd = Domain.Teams.Team.Create("Patterns R&D team", department: "Patterns R&D");
            var other = Domain.Teams.Team.Create("Patterns Other team", department: "Patterns Other");
            db.Teams.AddRange(rd, other);
            await db.SaveChangesAsync();

            var seededHead = await SeedEngineerAsync("fb_scope_head@vitals.io", Roles.HeadOfRnD);
            var head = await db.Engineers.FindAsync(seededHead.Id);
            head!.AssignToTeam(rd.Id);

            // 3 people in R&D (enough to show) and 3 in the other department (enough to show, but not theirs to see).
            for (var i = 0; i < 3; i++)
            {
                var e = Domain.Engineers.Engineer.Create($"Scope rd {i}", $"fb_scope_rd{i}@vitals.io", "hash", Roles.Engineer, 20, 14);
                e.AssignToTeam(rd.Id); db.Engineers.Add(e); rdEngineers.Add(e.Id);
                var o = Domain.Engineers.Engineer.Create($"Scope other {i}", $"fb_scope_other{i}@vitals.io", "hash", Roles.Engineer, 20, 14);
                o.AssignToTeam(other.Id); db.Engineers.Add(o); otherEngineers.Add(o.Id);
            }
            await db.SaveChangesAsync();
            foreach (var id in rdEngineers.Concat(otherEngineers))
                db.Feedback.Add(Feedback.Submit(id, "Scoped feedback", week));
            // The other department also has a week too thin to show.
            db.Feedback.Add(Feedback.Submit(otherEngineers[0], "Thin week", week.AddDays(7)));
            await db.SaveChangesAsync();
        }
        await SeedEngineerAsync("fb_scope_hr@vitals.io", Roles.HR);

        var headBody = await (await AuthenticatedClient(await LoginTokenAsync("fb_scope_head@vitals.io"))
            .GetAsync("/api/v1/feedback/patterns")).Content.ReadFromJsonAsync<ApiResponse<FeedbackPatternsDto>>(JsonOpts);
        var hrBody = await (await AuthenticatedClient(await LoginTokenAsync("fb_scope_hr@vitals.io"))
            .GetAsync("/api/v1/feedback/patterns")).Content.ReadFromJsonAsync<ApiResponse<FeedbackPatternsDto>>(JsonOpts);

        var mine = headBody!.Data!;
        mine.Scope.Should().Be("Patterns R&D");
        mine.Weeks.Single(w => w.WeekOf == week).TotalResponses.Should().Be(3, "only the head's own department is counted");
        mine.Weeks.Should().NotContain(w => w.WeekOf == week.AddDays(7));
        mine.EligiblePeople.Should().Be(3, "the head is not someone who can give feedback, and the other department is not theirs");

        var all = hrBody!.Data!;
        all.Scope.Should().Be("Organisation");
        all.Weeks.Single(w => w.WeekOf == week).TotalResponses.Should().Be(6);
    }

    // ── vitals access control ──────────────────────────────────────────────────

    [Fact]
    public async Task Engineer_can_submit_vitals()
    {
        await SeedEngineerAsync("vitals_submit@vitals.io");
        var token = await LoginTokenAsync("vitals_submit@vitals.io");
        var client = AuthenticatedClient(token);

        var response = await client.PostAsJsonAsync("/api/v1/vitals", new { score = 4, comment = "Good week" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Engineer_cannot_submit_vitals_twice_in_same_week()
    {
        await SeedEngineerAsync("vitals_dup@vitals.io");
        var token = await LoginTokenAsync("vitals_dup@vitals.io");
        var client = AuthenticatedClient(token);

        await client.PostAsJsonAsync("/api/v1/vitals", new { score = 3 });
        var second = await client.PostAsJsonAsync("/api/v1/vitals", new { score = 4 });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Engineer_cannot_read_vitals_list()
    {
        await SeedEngineerAsync("vitals_read_denied@vitals.io");
        var token = await LoginTokenAsync("vitals_read_denied@vitals.io");
        var client = AuthenticatedClient(token);

        var response = await client.GetAsync("/api/v1/vitals");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Head_can_read_vitals_list()
    {
        await SeedEngineerAsync("vitals_head@vitals.io", Roles.HeadOfRnD);
        var headToken = await LoginTokenAsync("vitals_head@vitals.io");
        var response = await AuthenticatedClient(headToken).GetAsync("/api/v1/vitals");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pulse_score_outside_range_is_rejected()
    {
        await SeedEngineerAsync("vitals_range@vitals.io");
        var token = await LoginTokenAsync("vitals_range@vitals.io");
        var client = AuthenticatedClient(token);

        var response = await client.PostAsJsonAsync("/api/v1/vitals", new { score = 6 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── override access control ───────────────────────────────────────────────

    [Fact]
    public async Task Engineer_can_create_override_for_themselves()
    {
        var engineer = await SeedEngineerAsync("override_self@vitals.io");
        var token = await LoginTokenAsync("override_self@vitals.io");
        var client = AuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/engineers/{engineer.Id}/override",
            new { reason = "Critical deadline this week" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Engineer_cannot_create_override_for_another_engineer()
    {
        var target = await SeedEngineerAsync("override_target@vitals.io");
        await SeedEngineerAsync("override_actor@vitals.io");
        var token = await LoginTokenAsync("override_actor@vitals.io");
        var client = AuthenticatedClient(token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/engineers/{target.Id}/override",
            new { reason = "Should not be allowed" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Engineer_can_view_own_override_history()
    {
        var engineer = await SeedEngineerAsync("override_hist@vitals.io");
        var token = await LoginTokenAsync("override_hist@vitals.io");
        var client = AuthenticatedClient(token);

        await client.PostAsJsonAsync($"/api/v1/engineers/{engineer.Id}/override",
            new { reason = "History test" });

        var response = await client.GetAsync($"/api/v1/engineers/{engineer.Id}/overrides");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<OverrideDto>>>(JsonOpts);
        body!.Data!.Should().HaveCountGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task Engineer_cannot_view_another_engineers_override_history()
    {
        var target = await SeedEngineerAsync("override_hist_target@vitals.io");
        await SeedEngineerAsync("override_hist_actor@vitals.io");
        var token = await LoginTokenAsync("override_hist_actor@vitals.io");
        var client = AuthenticatedClient(token);

        var response = await client.GetAsync($"/api/v1/engineers/{target.Id}/overrides");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
