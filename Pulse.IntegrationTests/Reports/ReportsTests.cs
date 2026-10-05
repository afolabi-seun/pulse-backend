using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Overwork;
using Pulse.Application.Reports;
using Pulse.Application.Reports.Queries;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Reports;

[Collection("Integration")]
public class ReportsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ReportsTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── access control ────────────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_cannot_get_leadership_report()
    {
        var response = await Client.GetAsync("/api/v1/reports/leadership");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Engineer_cannot_get_leadership_report()
    {
        await SeedEngineerAsync("report_eng_denied@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("report_eng_denied@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/leadership");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PM_cannot_get_leadership_report()
    {
        await SeedEngineerAsync("report_pm_denied@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("report_pm_denied@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/leadership");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    public async Task Executive_and_hr_can_get_leadership_report(string role)
    {
        await SeedEngineerAsync($"report_{role}_allowed@pulse.io", role);
        var client = await AuthenticatedClientAsync($"report_{role}_allowed@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/leadership");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hr_sees_leadership_report_org_wide_across_departments()
    {
        var rndTeam    = await SeedTeamAsync("Leadership RnD team", null, department: "RnD");
        var designTeam = await SeedTeamAsync("Leadership Design team", null, department: "Design");
        var rndEng     = await SeedEngineerAsync("leadership_rnd_eng@pulse.io", Roles.Engineer);
        var designEng  = await SeedEngineerAsync("leadership_design_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(rndEng.Id, rndTeam.Id);
        await AssignEngineerToTeamAsync(designEng.Id, designTeam.Id);
        await SeedEngineerAsync("leadership_hr_viewer@pulse.io", Roles.HR);
        var client = await AuthenticatedClientAsync("leadership_hr_viewer@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/leadership");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<LeadershipReportDto>>(JsonOpts);
        var names = body!.Data!.Engineers.Select(e => e.Name).ToList();
        names.Should().Contain(new[] { rndEng.Name, designEng.Name });
    }

    // ── leadership report JSON ────────────────────────────────────────────────

    [Fact]
    public async Task Head_can_get_leadership_report()
    {
        await SeedEngineerAsync("report_head@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("report_head@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/leadership");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<LeadershipReportDto>>(JsonOpts);
        body!.Status.Should().Be("success");
        body.Data.Should().NotBeNull();
        body.Data!.WeekOf.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Leadership_report_returns_engineer_entries()
    {
        await SeedEngineerAsync("report_head2@pulse.io", Roles.HeadOfRnD);
        await SeedEngineerAsync("report_member@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("report_head2@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/leadership");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<LeadershipReportDto>>(JsonOpts);
        body!.Data!.Engineers.Should().NotBeNull();
    }

    [Fact]
    public async Task Leadership_report_accepts_optional_weekOf_param()
    {
        await SeedEngineerAsync("report_head_weekof@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("report_head_weekof@pulse.io");
        var weekOf = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)).ToString("yyyy-MM-dd");

        var response = await client.GetAsync($"/api/v1/reports/leadership?weekOf={weekOf}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<LeadershipReportDto>>(JsonOpts);
        body!.Data!.WeekOf.Should().NotBeNullOrEmpty();
    }

    // ── leadership report PDF ─────────────────────────────────────────────────

    [Fact]
    public async Task Head_can_download_leadership_report_pdf()
    {
        await SeedEngineerAsync("report_pdf_head@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("report_pdf_head@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/leadership/pdf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Engineer_cannot_download_leadership_pdf()
    {
        await SeedEngineerAsync("report_pdf_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("report_pdf_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/leadership/pdf");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── personalized report (/me) ───────────────────────────────────────────────

    [Fact]
    public async Task Engineer_can_get_their_own_report()
    {
        await SeedEngineerAsync("report_me_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("report_me_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<OverworkSignalsDto>>(JsonOpts);
        body!.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task My_report_requires_authentication()
    {
        var response = await Client.GetAsync("/api/v1/reports/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── weekly team report ──────────────────────────────────────────────────────

    [Fact]
    public async Task Engineer_cannot_get_weekly_report()
    {
        var team = await SeedTeamAsync("Weekly Denied Team");
        var engineer = await SeedEngineerAsync("weekly_eng_denied@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var client = await AuthenticatedClientAsync("weekly_eng_denied@pulse.io");

        var response = await client.GetAsync($"/api/v1/reports/weekly?teamId={team.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Team_lead_can_view_own_teams_weekly_report_with_no_existing_draft()
    {
        var team = await SeedTeamAsync("Weekly Own Team");
        var lead = await SeedEngineerAsync("weekly_lead_own@pulse.io", Roles.TeamLead);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var client = await AuthenticatedClientAsync("weekly_lead_own@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/weekly");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<WeeklyReportDto>>(JsonOpts);
        body!.Data!.TeamId.Should().Be(team.Id);
        body.Data!.IsNew.Should().BeTrue();
        body.Data!.ExecutiveSummary.Should().BeEmpty();
    }

    [Fact]
    public async Task Team_lead_cannot_view_another_teams_weekly_report()
    {
        var ownTeam = await SeedTeamAsync("Weekly Lead's Own Team");
        var otherTeam = await SeedTeamAsync("Weekly Other Team");
        var lead = await SeedEngineerAsync("weekly_lead_other@pulse.io", Roles.TeamLead);
        await AssignEngineerToTeamAsync(lead.Id, ownTeam.Id);
        var client = await AuthenticatedClientAsync("weekly_lead_other@pulse.io");

        var response = await client.GetAsync($"/api/v1/reports/weekly?teamId={otherTeam.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task HeadOfPmo_without_teamId_gets_unprocessable_entity()
    {
        await SeedEngineerAsync("weekly_pmo_no_team@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("weekly_pmo_no_team@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/weekly");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task HeadOfPmo_can_view_any_teams_weekly_report()
    {
        var team = await SeedTeamAsync("Weekly PMO View Team");
        await SeedEngineerAsync("weekly_pmo_view@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("weekly_pmo_view@pulse.io");

        var response = await client.GetAsync($"/api/v1/reports/weekly?teamId={team.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<WeeklyReportDto>>(JsonOpts);
        body!.Data!.TeamId.Should().Be(team.Id);
    }

    [Fact]
    public async Task HeadOfPmo_can_save_and_submit_a_draft_for_any_team()
    {
        var team = await SeedTeamAsync("Weekly PMO Write Team");
        await SeedEngineerAsync("weekly_pmo_write@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("weekly_pmo_write@pulse.io");
        var weekOf = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        var saveResponse = await client.PutAsJsonAsync("/api/v1/reports/weekly/draft", new
        {
            teamId = team.Id, weekOf,
            executiveSummary = "Filed on behalf of the team.", keyAccomplishments = "x", plannedNextWeek = "x", resourcingNotes = "x"
        });
        saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var submitResponse = await client.PostAsJsonAsync("/api/v1/reports/weekly/submit", new { teamId = team.Id, weekOf });
        submitResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var submitted = await submitResponse.Content.ReadFromJsonAsync<ApiResponse<WeeklyReportDto>>(JsonOpts);
        submitted!.Data!.SubmittedByName.Should().Be("Test User");
    }

    [Fact]
    public async Task Team_lead_can_save_and_retrieve_a_draft()
    {
        var team = await SeedTeamAsync("Weekly Save Team");
        var lead = await SeedEngineerAsync("weekly_lead_save@pulse.io", Roles.TeamLead);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var client = await AuthenticatedClientAsync("weekly_lead_save@pulse.io");
        var weekOf = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        var saveResponse = await client.PutAsJsonAsync("/api/v1/reports/weekly/draft", new
        {
            teamId = team.Id,
            weekOf,
            executiveSummary = "Shipped the widget redesign.",
            keyAccomplishments = "Fixed 3 bugs",
            plannedNextWeek = "Ship v2",
            resourcingNotes = "Fully staffed"
        });

        saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await saveResponse.Content.ReadFromJsonAsync<ApiResponse<WeeklyReportDto>>(JsonOpts);
        saved!.Data!.ExecutiveSummary.Should().Be("Shipped the widget redesign.");
        saved.Data!.IsNew.Should().BeFalse();

        var getResponse = await client.GetAsync("/api/v1/reports/weekly");
        var fetched = await getResponse.Content.ReadFromJsonAsync<ApiResponse<WeeklyReportDto>>(JsonOpts);
        fetched!.Data!.ExecutiveSummary.Should().Be("Shipped the widget redesign.");
        fetched.Data!.ResourcingNotes.Should().Be("Fully staffed");
    }

    [Fact]
    public async Task Non_team_lead_cannot_save_a_draft()
    {
        var team = await SeedTeamAsync("Weekly PM Save Team");
        var pm = await SeedEngineerAsync("weekly_pm_save@pulse.io", Roles.ProjectManager);
        await AssignEngineerToTeamAsync(pm.Id, team.Id);
        var client = await AuthenticatedClientAsync("weekly_pm_save@pulse.io");
        var weekOf = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        var response = await client.PutAsJsonAsync("/api/v1/reports/weekly/draft", new
        {
            teamId = team.Id, weekOf, executiveSummary = "x", keyAccomplishments = "x", plannedNextWeek = "x", resourcingNotes = "x"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Submitting_without_a_saved_draft_returns_not_found()
    {
        var team = await SeedTeamAsync("Weekly No Draft Team");
        var lead = await SeedEngineerAsync("weekly_lead_nodraft@pulse.io", Roles.TeamLead);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var client = await AuthenticatedClientAsync("weekly_lead_nodraft@pulse.io");
        var weekOf = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        var response = await client.PostAsJsonAsync("/api/v1/reports/weekly/submit", new { teamId = team.Id, weekOf });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Team_lead_can_submit_a_saved_draft_and_editing_afterward_clears_the_sign_off()
    {
        var team = await SeedTeamAsync("Weekly Submit Team");
        var lead = await SeedEngineerAsync("weekly_lead_submit@pulse.io", Roles.TeamLead);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var client = await AuthenticatedClientAsync("weekly_lead_submit@pulse.io");
        var weekOf = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        await client.PutAsJsonAsync("/api/v1/reports/weekly/draft", new
        {
            teamId = team.Id, weekOf, executiveSummary = "v1", keyAccomplishments = "x", plannedNextWeek = "x", resourcingNotes = "x"
        });

        var submitResponse = await client.PostAsJsonAsync("/api/v1/reports/weekly/submit", new { teamId = team.Id, weekOf });
        submitResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var submitted = await submitResponse.Content.ReadFromJsonAsync<ApiResponse<WeeklyReportDto>>(JsonOpts);
        submitted!.Data!.SubmittedAt.Should().NotBeNull();
        submitted.Data!.SubmittedByName.Should().Be("Test User");

        var editResponse = await client.PutAsJsonAsync("/api/v1/reports/weekly/draft", new
        {
            teamId = team.Id, weekOf, executiveSummary = "v2", keyAccomplishments = "x", plannedNextWeek = "x", resourcingNotes = "x"
        });
        var edited = await editResponse.Content.ReadFromJsonAsync<ApiResponse<WeeklyReportDto>>(JsonOpts);
        edited!.Data!.ExecutiveSummary.Should().Be("v2");
        edited.Data!.SubmittedAt.Should().BeNull();
    }

    [Fact]
    public async Task Team_lead_can_download_the_weekly_report_as_docx()
    {
        var team = await SeedTeamAsync("Weekly Docx Team");
        var lead = await SeedEngineerAsync("weekly_lead_docx@pulse.io", Roles.TeamLead);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var client = await AuthenticatedClientAsync("weekly_lead_docx@pulse.io");
        var weekOf = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        await client.PutAsJsonAsync("/api/v1/reports/weekly/draft", new
        {
            teamId = team.Id,
            weekOf,
            executiveSummary = "Shipped the widget redesign this week.",
            // More lines than the template's 4 placeholder bullets, to exercise row/paragraph cloning.
            keyAccomplishments = "Fixed 3 bugs\nShipped v2\nOnboarded a new engineer\nClosed the security review\nWrote onboarding docs",
            plannedNextWeek = "Ship v3",
            resourcingNotes = "Fully staffed"
        });

        var response = await client.GetAsync("/api/v1/reports/weekly/docx");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);

        using var stream = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(stream, false);
        var bodyText = string.Concat(doc.MainDocumentPart!.Document.Body!.Descendants<Text>().Select(t => t.Text));

        bodyText.Should().Contain("Weekly Docx Team");
        bodyText.Should().Contain("Shipped the widget redesign this week.");
        bodyText.Should().Contain("Fixed 3 bugs");
        bodyText.Should().Contain("Wrote onboarding docs"); // 5th bullet — only present if row-cloning worked
        bodyText.Should().Contain("Fully staffed");
    }

    // ── pmo report scoping ────────────────────────────────────────────────────

    [Fact]
    public async Task PmoReport_scopes_projects_to_the_callers_own_team_for_a_department_head()
    {
        var teamA = await SeedTeamAsync("PMO Scope Team A");
        var teamB = await SeedTeamAsync("PMO Scope Team B");
        var head  = await SeedEngineerAsync("pmo_scope_head@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(head.Id, teamA.Id);
        await SeedProjectAsync("Team A project", teamA.Id);
        await SeedProjectAsync("Team B project", teamB.Id);
        var client = await AuthenticatedClientAsync("pmo_scope_head@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/pmo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        body!.Data!.Projects.Should().Contain(p => p.Name == "Team A project");
        body.Data.Projects.Should().NotContain(p => p.Name == "Team B project");
    }

    [Fact]
    public async Task PmoReport_includes_an_out_of_department_project_the_head_is_a_member_of()
    {
        // A head who's been explicitly added to a project outside their own department (and even
        // has tasks there) must not find it silently missing from their own Project Health table.
        var teamA = await SeedTeamAsync("PMO Member Team A");
        var teamB = await SeedTeamAsync("PMO Member Team B");
        var head  = await SeedEngineerAsync("pmo_member_head@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(head.Id, teamA.Id);
        await SeedProjectAsync("Team A project", teamA.Id);
        var otherProject = await SeedProjectAsync("Team B project", teamB.Id);
        await SeedProjectMemberAsync(otherProject.Id, head.Id);
        var client = await AuthenticatedClientAsync("pmo_member_head@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/pmo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        body!.Data!.Projects.Should().Contain(p => p.Name == "Team B project");
    }

    [Fact]
    public async Task PmoReport_includes_an_out_of_department_project_the_head_follows()
    {
        var teamA = await SeedTeamAsync("PMO Follow Team A");
        var teamB = await SeedTeamAsync("PMO Follow Team B");
        var head    = await SeedEngineerAsync("pmo_follow_head@pulse.io", Roles.HeadOfRnD);
        var report  = await SeedEngineerAsync("pmo_follow_head_report@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(head.Id, teamA.Id);
        await AssignEngineerToTeamAsync(report.Id, teamA.Id);
        await SeedProjectAsync("Team A project", teamA.Id);
        var otherProject = await SeedProjectAsync("Team B project", teamB.Id);
        // FollowProjectCommand requires one of the head's own engineers to already be a member.
        await SeedProjectMemberAsync(otherProject.Id, report.Id);
        var client = await AuthenticatedClientAsync("pmo_follow_head@pulse.io");

        var followResponse = await client.PostAsJsonAsync($"/api/v1/projects/{otherProject.Id}/follow", new { });
        followResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.GetAsync("/api/v1/reports/pmo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        body!.Data!.Projects.Should().Contain(p => p.Name == "Team B project");
    }

    [Fact]
    public async Task PmoReport_shows_every_project_for_HeadOfPmo()
    {
        var teamA = await SeedTeamAsync("PMO Org Team A");
        var teamB = await SeedTeamAsync("PMO Org Team B");
        await SeedEngineerAsync("pmo_scope_pmo@pulse.io", Roles.HeadOfPmo);
        await SeedProjectAsync("Org project A", teamA.Id);
        await SeedProjectAsync("Org project B", teamB.Id);
        var client = await AuthenticatedClientAsync("pmo_scope_pmo@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/pmo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        body!.Data!.Projects.Should().Contain(p => p.Name == "Org project A");
        body.Data.Projects.Should().Contain(p => p.Name == "Org project B");
    }

    [Fact]
    public async Task PmoReport_shows_every_project_for_a_ProjectManager_with_no_team_of_their_own()
    {
        // Regression: ProjectManager (a genuinely org-wide role, with no single team) was missing
        // from the handler's "org-wide" role check, so it fell into the single-team-scoping branch,
        // resolved a null team, and always got an empty report — for every PM, every week.
        var teamA = await SeedTeamAsync("PMO PM Team A");
        var teamB = await SeedTeamAsync("PMO PM Team B");
        await SeedEngineerAsync("pmo_scope_pm@pulse.io", Roles.ProjectManager);
        await SeedProjectAsync("PM-visible project A", teamA.Id);
        await SeedProjectAsync("PM-visible project B", teamB.Id);
        var client = await AuthenticatedClientAsync("pmo_scope_pm@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/pmo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        body!.Data!.Projects.Should().Contain(p => p.Name == "PM-visible project A");
        body.Data.Projects.Should().Contain(p => p.Name == "PM-visible project B");
    }

    [Fact]
    public async Task PmoReport_excludes_a_project_manager_team_member_from_team_utilization()
    {
        var team = await SeedTeamAsync("PMO Utilization Team");
        var engineer = await SeedEngineerAsync("pmo_util_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var pmOnTeam = await SeedEngineerAsync("pmo_util_pm@pulse.io", Roles.ProjectManager);
        await AssignEngineerToTeamAsync(pmOnTeam.Id, team.Id);
        await SeedEngineerAsync("pmo_util_caller@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("pmo_util_caller@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/pmo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        var teamEntry = body!.Data!.Teams.Should().ContainSingle(t => t.TeamId == team.Id).Subject;
        teamEntry.Engineers.Should().Contain(e => e.EngineerId == engineer.Id);
        teamEntry.Engineers.Should().NotContain(e => e.EngineerId == pmOnTeam.Id);
    }

    [Fact]
    public async Task PmoReport_hours_respect_an_explicit_from_and_to_not_aligned_to_a_calendar_week()
    {
        // Hours used to stay silently pinned to the calendar week containing "to" no matter what
        // range was requested — "Points delivered" would reflect the custom range while every
        // Hours column on the same report kept showing just one week's worth. Logged as a
        // task-category entry deliberately: Team Utilization's Hours column is delivery-only (see
        // GetDeliveryHoursByEngineerInRangeAsync), so a Meeting/Admin/Leave/Other entry wouldn't
        // count toward it at all.
        var team = await SeedTeamAsync("PMO Range Hours Team");
        var engineer = await SeedEngineerAsync("pmo_range_hours_eng@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("PMO Range Hours Project", team.Id);
        var task = await SeedTaskAsync("PMO range hours task", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("pmo_range_hours_eng@pulse.io");
        await SeedEngineerAsync("pmo_range_hours_caller@pulse.io", Roles.HeadOfPmo);
        var pmoClient = await AuthenticatedClientAsync("pmo_range_hours_caller@pulse.io");

        // Well outside the current calendar week, but inside a wide custom range.
        var loggedDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-20);
        var logResp = await client.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = loggedDate.ToString("yyyy-MM-dd"), category = "task", taskId = task.Id, hours = 4,
        });
        logResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var from = loggedDate.AddDays(-5).ToString("yyyy-MM-dd");
        var to = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var response = await pmoClient.GetAsync($"/api/v1/reports/pmo?from={from}&to={to}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        body!.Data!.DeliveredFrom.Should().Be(from);
        body.Data.DeliveredTo.Should().Be(to);
        var teamEntry = body.Data.Teams.Should().ContainSingle(t => t.TeamId == team.Id).Subject;
        var engEntry = teamEntry.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id).Subject;
        engEntry.HoursLoggedThisWeek.Should().Be(4);
    }

    // ── pmo report CSV export ───────────────────────────────────────────────────

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    public async Task Executive_and_hr_can_download_pmo_report_csv(string role)
    {
        // The CSV export previously only mirrored the JSON endpoint's TeamLeadOrAbove access, not
        // its ExecutiveRead/HrRead grant — Executive/HR could view the report on screen but not
        // export it.
        await SeedEngineerAsync($"pmo_csv_{role}@pulse.io", role);
        var client = await AuthenticatedClientAsync($"pmo_csv_{role}@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/pmo/csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
    }

    [Fact]
    public async Task Engineer_cannot_download_pmo_report_csv()
    {
        await SeedEngineerAsync("pmo_csv_eng_denied@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("pmo_csv_eng_denied@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/pmo/csv");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── org trend ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrgTrend_returns_200_for_Executive()
    {
        await SeedEngineerAsync("org_trend_exec@pulse.io", Roles.Executive);
        var client = await AuthenticatedClientAsync("org_trend_exec@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/org-trend");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<OrgTrendDto>>(JsonOpts);
        body!.Status.Should().Be("success");
    }

    [Fact]
    public async Task OrgTrend_returns_403_for_a_role_below_executive()
    {
        await SeedEngineerAsync("org_trend_pmo@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("org_trend_pmo@pulse.io");

        var response = await client.GetAsync("/api/v1/reports/org-trend");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OrgTrend_aggregates_delivered_points_across_teams()
    {
        var teamA = await SeedTeamAsync("Org Trend Team A");
        var teamB = await SeedTeamAsync("Org Trend Team B");
        var engineerA = await SeedEngineerAsync("org_trend_eng_a@pulse.io", Roles.Engineer);
        var engineerB = await SeedEngineerAsync("org_trend_eng_b@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineerA.Id, teamA.Id);
        await AssignEngineerToTeamAsync(engineerB.Id, teamB.Id);
        var projectA = await SeedProjectAsync("Org Trend Project A", teamA.Id);
        var projectB = await SeedProjectAsync("Org Trend Project B", teamB.Id);
        var clientA = await AuthenticatedClientAsync("org_trend_eng_a@pulse.io");
        var clientB = await AuthenticatedClientAsync("org_trend_eng_b@pulse.io");

        var taskA = await SeedTaskAsync("Org trend task A", projectA.Id, points: 5, assigneeId: engineerA.Id);
        var taskB = await SeedTaskAsync("Org trend task B", projectB.Id, points: 8, assigneeId: engineerB.Id);
        await clientA.PostAsync($"/api/v1/tasks/{taskA.Id}/mark-done", null);
        await clientB.PostAsync($"/api/v1/tasks/{taskB.Id}/mark-done", null);

        await SeedEngineerAsync("org_trend_exec_agg@pulse.io", Roles.Executive);
        var execClient = await AuthenticatedClientAsync("org_trend_exec_agg@pulse.io");

        var response = await execClient.GetAsync("/api/v1/reports/org-trend");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<OrgTrendDto>>(JsonOpts);
        body!.Data!.DeliveryTrend.Sum(p => p.PointsDelivered).Should().BeGreaterOrEqualTo(13);
    }

    // ── project health: high-priority tasks ──────────────────────────────────

    [Fact]
    public async Task PmoReport_counts_only_open_high_priority_tasks_per_project()
    {
        await SeedEngineerAsync("pmo_priority_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Priority report project");
        var highPriorityOpen = await SeedTaskAsync("High priority open task", project.Id);
        var highPriorityDone = await SeedTaskAsync("High priority done task", project.Id);
        var lowPriorityOpen = await SeedTaskAsync("Low priority open task", project.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Tasks.FindAsync(highPriorityOpen.Id))!.SetPriority(5);
            var done = (await db.Tasks.FindAsync(highPriorityDone.Id))!;
            done.SetPriority(4);
            (await db.Tasks.FindAsync(lowPriorityOpen.Id))!.SetPriority(2);
            await db.SaveChangesAsync();
        }

        var pmClient = await AuthenticatedClientAsync("pmo_priority_pm@pulse.io");
        await pmClient.PostAsync($"/api/v1/tasks/{highPriorityDone.Id}/mark-done", null);

        var response = await pmClient.GetAsync("/api/v1/reports/pmo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);
        body!.Data!.Projects.Should().ContainSingle(p => p.ProjectId == project.Id && p.HighPriorityOpenTasks == 1);
    }
}
