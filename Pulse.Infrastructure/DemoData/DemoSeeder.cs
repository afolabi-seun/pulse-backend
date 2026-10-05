using Pulse.Application.Common.Interfaces;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Engineers;
using Pulse.Domain.Epics;
using Pulse.Domain.Escalations;
using Pulse.Domain.Feedback;
using Pulse.Domain.Notifications;
using Pulse.Domain.Projects;
using Pulse.Domain.Vitals;
using Pulse.Domain.Sprints;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using Pulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.DemoData;

public sealed class DemoSeeder(PulseDbContext db, IPasswordHasher hasher) : IDemoSeeder
{
    private const string DemoPassword = "Demo@12345!";

    private static readonly string TruncateSql = @"
        TRUNCATE TABLE
            audit_log, notifications, escalation_events, overwork_overrides,
            vitals_responses, feedback, check_ins, task_history, tasks, epics, sprints,
            refresh_tokens, engineers, teams, projects, threshold_settings
        RESTART IDENTITY CASCADE";

    public async Task ClearAsync(CancellationToken ct = default)
        => await db.Database.ExecuteSqlRawAsync(TruncateSql, ct);

    public async Task<DemoSeedSummary> SeedAsync(CancellationToken ct = default)
    {
        // ── 1. Wipe everything ──────────────────────────────────────────────────
        await db.Database.ExecuteSqlRawAsync(TruncateSql, ct);

        var today       = DateOnly.FromDateTime(DateTime.UtcNow);
        var monday      = GetMonday(today);
        var lastMonday  = monday.AddDays(-7);
        var twoWeeksAgo = monday.AddDays(-14);

        var hash = await Task.Run(() => hasher.Hash(DemoPassword), ct);

        // ── 2. Engineers ─────────────────────────────────────────────────────────
        var alice   = Engineer.Create("Alice Chen",    "alice@pulse.demo",   hash, Roles.HeadOfRnD,      20, 5);
        var diana   = Engineer.Create("Diana Park",    "diana@pulse.demo",   hash, Roles.HeadOfProduct,  18, 5);
        var charlie = Engineer.Create("Charlie Webb",  "charlie@pulse.demo", hash, Roles.HeadOfDesign,   16, 4);
        var nina    = Engineer.Create("Nina Torres",   "nina@pulse.demo",    hash, Roles.HeadOfPmo,      15, 4);
        var priya   = Engineer.Create("Priya Kapoor",  "priya@pulse.demo",   hash, Roles.HeadOfCoreBanking, 12, 4);
        var felix   = Engineer.Create("Felix Onen",    "felix@pulse.demo",   hash, Roles.HeadOfInfraDevOps, 12, 4);
        // Executive/HR never carry estimated task work, so this baseline is inert — but
        // Engineer.Create now rejects a non-positive value (see ValidateBaseline), matching what
        // CreateUserRequestValidator already required on the real "add user" form.
        var olivia  = Engineer.Create("Olivia Sterling", "olivia@pulse.demo", hash, Roles.Executive,      1,  4);
        var hannah  = Engineer.Create("Hannah Reyes",  "hannah@pulse.demo",  hash, Roles.HR,             1,  4);
        var bob     = Engineer.Create("Bob Martinez",  "bob@pulse.demo",     hash, Roles.ProjectManager, 15, 4);
        var carol   = Engineer.Create("Carol Singh",   "carol@pulse.demo",   hash, Roles.TeamLead,       18, 4);
        var david   = Engineer.Create("David Kim",     "david@pulse.demo",   hash, Roles.TeamLead,       16, 5);
        var emma    = Engineer.Create("Emma Wilson",   "emma@pulse.demo",    hash, Roles.Engineer,       12, 3);
        var frank   = Engineer.Create("Frank Okafor",  "frank@pulse.demo",   hash, Roles.Engineer,       14, 4);
        var grace   = Engineer.Create("Grace Liu",     "grace@pulse.demo",   hash, Roles.Engineer,       10, 3);
        var henry   = Engineer.Create("Henry Patel",   "henry@pulse.demo",   hash, Roles.Engineer,       15, 4);
        var iris    = Engineer.Create("Iris Nakamura", "iris@pulse.demo",    hash, Roles.Engineer,       12, 3);
        var james   = Engineer.Create("James Murphy",  "james@pulse.demo",   hash, Roles.Engineer,       13, 4);
        var leo     = Engineer.Create("Leo Barnes",    "leo@pulse.demo",     hash, Roles.Designer,        9, 3);
        var mia     = Engineer.Create("Mia Osei",      "mia@pulse.demo",     hash, Roles.Engineer,        8, 3);

        var engineers = new[] { alice, diana, charlie, nina, priya, felix, olivia, hannah, bob, carol, david,
                                 emma, frank, grace, henry, iris, james, leo, mia };
        db.Engineers.AddRange(engineers);
        await db.SaveChangesAsync(ct);

        // ── 3. Teams ─────────────────────────────────────────────────────────────
        var platform = Team.Create("Platform Team", carol.Id);
        platform.SetDepartment("Engineering");
        var product  = Team.Create("Product Team",  david.Id);
        product.SetDepartment("Product");
        var design   = Team.Create("Design Team",   charlie.Id);
        design.SetDepartment("Design");
        var pmo      = Team.Create("PMO Office",    bob.Id);
        pmo.SetDepartment("PMO");
        var coreBanking = Team.Create("Core Banking Team", priya.Id);
        coreBanking.SetDepartment("Core Banking");
        var infraDevOps = Team.Create("Infra/DevOps Team", felix.Id);
        infraDevOps.SetDepartment("Infra/DevOps");
        db.Teams.AddRange(platform, product, design, pmo, coreBanking, infraDevOps);
        await db.SaveChangesAsync(ct);

        // ── 4. Assign engineers to teams ─────────────────────────────────────────
        carol.AssignToTeam(platform.Id); carol.SetTeam(platform.Name);
        emma.AssignToTeam(platform.Id);  emma.SetTeam(platform.Name);
        frank.AssignToTeam(platform.Id); frank.SetTeam(platform.Name);
        grace.AssignToTeam(platform.Id); grace.SetTeam(platform.Name);

        david.AssignToTeam(product.Id);  david.SetTeam(product.Name);
        henry.AssignToTeam(product.Id);  henry.SetTeam(product.Name);
        iris.AssignToTeam(product.Id);   iris.SetTeam(product.Name);
        james.AssignToTeam(product.Id);  james.SetTeam(product.Name);

        leo.AssignToTeam(design.Id);     leo.SetTeam(design.Name);
        mia.AssignToTeam(pmo.Id);        mia.SetTeam(pmo.Name);

        // Department heads who actually carry team-scoped work (R&D, Design) join their own
        // department's team, so the department-scoped views (PMO report, Dashboard's
        // Organization section) have real data instead of an empty state. HeadOfPmo/HeadOfProduct
        // are unscoped by role already (see GetPmoReportHandler) and don't need a team for that.
        alice.AssignToTeam(platform.Id); alice.SetTeam(platform.Name);
        charlie.AssignToTeam(design.Id); charlie.SetTeam(design.Name);
        priya.AssignToTeam(coreBanking.Id); priya.SetTeam(coreBanking.Name);
        felix.AssignToTeam(infraDevOps.Id); felix.SetTeam(infraDevOps.Name);
        await db.SaveChangesAsync(ct);

        // ── 5. Projects ───────────────────────────────────────────────────────────
        var apiProject    = Project.Create("API Modernisation",  "Migrate legacy REST endpoints to versioned API with OpenAPI specs.", code: "API");
        var mobileProject = Project.Create("Mobile App v2",      "Full rewrite of the iOS/Android consumer app.", code: "MOBILE");
        var dashProject   = Project.Create("Internal Dashboard", "Engineering velocity dashboard for R&D leadership.", code: "DASH");
        var legacyProject = Project.Create("Legacy Migration",   "EOL the on-prem monolith and complete cloud migration.", code: "LEGACY");
        legacyProject.Archive();
        var cibProject = Project.Create("CIB",
            "Corporate Internet Banking platform — admin management, authentication, payments, approvals, standing orders, statements, bulk payments, and compliance.", code: "CIB");
        var omsProject = Project.Create("OMS",
            "Output Management System — multi-channel notification platform for fintech tenants covering SMS, email, and WhatsApp delivery, template management, analytics, and bulk campaigns.", code: "OMS");
        var regReportingProject = Project.Create("Regulatory Reporting",
            "Automated regulatory filings and compliance reporting for retail banking operations.", code: "REGREP");
        var platformReliabilityProject = Project.Create("Platform Reliability",
            "CI/CD pipeline hardening, observability, and infrastructure-as-code for the platform's production environment.", code: "PLATREL");

        db.Projects.AddRange(apiProject, mobileProject, dashProject, legacyProject, cibProject, omsProject, regReportingProject, platformReliabilityProject);
        await db.SaveChangesAsync(ct);

        // ── 5·1 Project ownership (drives department-head access via team → department) ─
        // Engineering (R&D) owns the core platform/banking work; Product owns OMS;
        // Design owns the mobile app; Core Banking owns regulatory reporting; Infra/DevOps owns
        // platform reliability. Lets each department head's scoped views resolve.
        apiProject.SetOwnerTeam(platform.Id);
        cibProject.SetOwnerTeam(platform.Id);
        dashProject.SetOwnerTeam(platform.Id);
        legacyProject.SetOwnerTeam(platform.Id);
        omsProject.SetOwnerTeam(product.Id);
        mobileProject.SetOwnerTeam(design.Id);
        regReportingProject.SetOwnerTeam(coreBanking.Id);
        platformReliabilityProject.SetOwnerTeam(infraDevOps.Id);
        await db.SaveChangesAsync(ct);

        // ── 5a. Epics ─────────────────────────────────────────────────────────────
        var cibAdmin    = Epic.Create("Admin & User Management",    cibProject.Id, order: 1);
        var cibAuth     = Epic.Create("Authentication",             cibProject.Id, order: 2);
        var cibPayments = Epic.Create("Payments & Transfers",       cibProject.Id, order: 3);
        var cibBulk     = Epic.Create("Bulk Payments",              cibProject.Id, order: 4);
        var cibApproval = Epic.Create("Approval Workflows",         cibProject.Id, order: 5);
        var cibStanding = Epic.Create("Standing Orders",            cibProject.Id, order: 6);

        var omsAuthRbac  = Epic.Create("Identity Authentication & RBAC",        omsProject.Id, order: 1);
        var omsIngestion = Epic.Create("Event Ingestion & Processing Pipeline", omsProject.Id, order: 2);
        var omsTemplates = Epic.Create("Template Management",                   omsProject.Id, order: 3);
        var omsSms       = Epic.Create("SMS Delivery Engine",                   omsProject.Id, order: 4);
        var omsWhatsApp  = Epic.Create("WhatsApp Delivery Engine",              omsProject.Id, order: 5);
        var omsCampaigns = Epic.Create("Bulk Campaigns & Scheduled Messaging",  omsProject.Id, order: 6);

        var regFilings = Epic.Create("Regulatory Filings", regReportingProject.Id, order: 1);
        var platformObservability = Epic.Create("Observability & Alerting", platformReliabilityProject.Id, order: 1);

        db.Epics.AddRange(
            cibAdmin, cibAuth, cibPayments, cibBulk, cibApproval, cibStanding,
            omsAuthRbac, omsIngestion, omsTemplates, omsSms, omsWhatsApp, omsCampaigns,
            regFilings, platformObservability);
        await db.SaveChangesAsync(ct);

        // ── 6. Sprints (2-week pulse, Mon–Fri) ──────────────────────────────────
        // Anchored so each sprint starts on a Monday:
        //   N-2 started 33 days ago, ended 22 days ago
        //   N-1 started 19 days ago, ended 8 days ago
        //   N   started 5 days ago,  ends 6 days from now
        //   N+1 starts 9 days from now
        var s12Start = today.AddDays(-33);
        var s12End   = today.AddDays(-22);
        var s13Start = today.AddDays(-19);
        var s13End   = today.AddDays(-8);
        var s14Start = today.AddDays(-5);
        var s14End   = today.AddDays(+6);
        var s15Start = today.AddDays(+9);
        var s15End   = today.AddDays(+20);

        // Platform Team: S12 (~85% delivery), S13 (crisis ~42%), S14 (active), S15 (planning)
        var s12 = Sprint.Create(platform.Id, cibProject.Id, "Sprint 12", s12Start, s12End, "Auth module stabilisation");
        s12.Activate(); s12.Complete();

        var s13 = Sprint.Create(platform.Id, cibProject.Id, "Sprint 13", s13Start, s13End, "Rate limiting & connection pool");
        s13.Activate(); s13.Complete();

        var s14 = Sprint.Create(platform.Id, cibProject.Id, "Sprint 14", s14Start, s14End, "API gateway & service mesh");
        s14.Activate();

        var s15 = Sprint.Create(platform.Id, cibProject.Id, "Sprint 15", s15Start, s15End, "gRPC migration — phase 2");

        // Product Team: S6 (~77% delivery), S7 (0% — production crisis), S8 (active recovery), S9 (planning)
        var s6 = Sprint.Create(product.Id, omsProject.Id, "Sprint 6", s12Start, s12End, "Onboarding & event ingestion");
        s6.Activate(); s6.Complete();

        var s7 = Sprint.Create(product.Id, omsProject.Id, "Sprint 7", s13Start, s13End, "Analytics pipeline & social auth");
        s7.Activate(); s7.Complete();

        var s8 = Sprint.Create(product.Id, omsProject.Id, "Sprint 8", s14Start, s14End, "Incident recovery — analytics & auth");
        s8.Activate();

        var s9 = Sprint.Create(product.Id, omsProject.Id, "Sprint 9", s15Start, s15End, "WhatsApp delivery engine");

        db.Sprints.AddRange(s12, s13, s14, s15, s6, s7, s8, s9);
        await db.SaveChangesAsync(ct);

        // ── 7. Tasks ──────────────────────────────────────────────────────────────
        var tasks = new List<PulseTask>();

        // ── Platform Sprint 12 (DONE, 40 pts, 34 done = 85%) ─────────────────────
        // Smooth sprint: auth stabilisation delivered on schedule, two minor items carried.
        Mk(tasks, "Migrate CIB auth endpoints to API v2",      8, s12End, cibProject.Id,
            TaskType.Feature, emma.Id,  alice.Id, s12.Id, epicId: cibAuth.Id,
            doneAt: Day(s12Start, 1));
        Mk(tasks, "Implement JWT refresh token rotation",       8, s12End, cibProject.Id,
            TaskType.Feature, frank.Id, alice.Id, s12.Id, epicId: cibAuth.Id,
            doneAt: Day(s12Start, 3));
        Mk(tasks, "Fix session expiry race condition",          5, s12End, cibProject.Id,
            TaskType.Bug,     grace.Id, alice.Id, s12.Id, epicId: cibAuth.Id,
            severity: BugSeverity.Medium, doneAt: Day(s12Start, 4));
        Mk(tasks, "Build admin user provisioning API",          8, s12End, cibProject.Id,
            TaskType.Feature, carol.Id, alice.Id, s12.Id, epicId: cibAdmin.Id,
            doneAt: Day(s12Start, 7));
        Mk(tasks, "Auth flow end-to-end test coverage",        5, s12End, cibProject.Id,
            TaskType.Test,    emma.Id,  alice.Id, s12.Id, epicId: cibAuth.Id,
            doneAt: Day(s12Start, 9));
        Mk(tasks, "Auth module code review",                   3, s12End, cibProject.Id,
            TaskType.Review,  frank.Id, alice.Id, s12.Id, epicId: cibAuth.Id);
        Mk(tasks, "Update CIB authentication docs",            3, s12End, cibProject.Id,
            TaskType.Chore,   grace.Id, alice.Id, s12.Id, epicId: cibAdmin.Id);

        // ── Platform Sprint 13 (DONE, 50 pts, 21 done = 42% — crisis sprint) ──────
        // Week 1: two reviews and a chore shipped. Day 4: critical production login failure
        // surfaced. Feature work abandoned; both critical bugs fixed in week 2.
        var p13_t1 = Mk(tasks, "Build rate limiting service",              8, s13End.AddDays(-3), cibProject.Id,
            TaskType.Feature, emma.Id,  alice.Id, s13.Id, epicId: cibPayments.Id,
            blocker: "Infra team hasn't provisioned the load balancer yet");
        Mk(tasks, "Connection pool manager for CIB database",              8, s13End, cibProject.Id,
            TaskType.Feature, frank.Id, alice.Id, s13.Id, epicId: cibPayments.Id);
        Mk(tasks, "Async job queue for payment approvals",                 8, s13End, cibProject.Id,
            TaskType.Feature, carol.Id, alice.Id, s13.Id, epicId: cibApproval.Id);
        Mk(tasks, "CRITICAL: Production login failures",                   5, s13End.AddDays(-3), cibProject.Id,
            TaskType.Bug,     emma.Id,  alice.Id, s13.Id, epicId: cibAuth.Id,
            severity: BugSeverity.Critical, doneAt: Day(s13Start, 7));
        Mk(tasks, "HIGH: Memory leak under sustained load",                5, s13End.AddDays(-2), cibProject.Id,
            TaskType.Bug,     frank.Id, alice.Id, s13.Id, epicId: cibPayments.Id,
            severity: BugSeverity.High, doneAt: Day(s13Start, 9));
        Mk(tasks, "Rate limiting design doc review",                       3, s13End.AddDays(-7), cibProject.Id,
            TaskType.Review,  grace.Id, alice.Id, s13.Id, epicId: cibPayments.Id,
            doneAt: Day(s13Start, 1));
        Mk(tasks, "Security threat model review",                          3, s13End.AddDays(-6), cibProject.Id,
            TaskType.Review,  carol.Id, alice.Id, s13.Id, epicId: cibAuth.Id,
            doneAt: Day(s13Start, 2));
        Mk(tasks, "Dependency vulnerability scan",                         5, s13End.AddDays(-5), cibProject.Id,
            TaskType.Chore,   grace.Id, alice.Id, s13.Id, epicId: cibAdmin.Id,
            doneAt: Day(s13Start, 3));
        Mk(tasks, "Load testing suite",                                    3, s13End, cibProject.Id,
            TaskType.Test,    emma.Id,  alice.Id, s13.Id, epicId: cibPayments.Id);
        Mk(tasks, "CIB health check endpoints",                            5, s13End, cibProject.Id,
            TaskType.Feature, frank.Id, alice.Id, s13.Id, epicId: cibAdmin.Id);

        // ── Platform Sprint 14 (ACTIVE, 48 pts, 20 done after 5 working days) ─────
        // Recovering sprint: gateway & health monitoring shipped early; gRPC in progress;
        // one blocked bug stalling the upstream service team.
        Mk(tasks, "API gateway routing configuration",         8, s14End.AddDays(-3), cibProject.Id,
            TaskType.Feature, carol.Id, alice.Id, s14.Id, epicId: cibApproval.Id,
            doneAt: Day(s14Start, 1));
        Mk(tasks, "Service health monitoring dashboard",       8, s14End.AddDays(-2), cibProject.Id,
            TaskType.Feature, emma.Id,  alice.Id, s14.Id, epicId: cibAdmin.Id,
            doneAt: Day(s14Start, 2));
        Mk(tasks, "Gateway routing configuration review",      4, s14End.AddDays(-2), cibProject.Id,
            TaskType.Review,  frank.Id, alice.Id, s14.Id, epicId: cibApproval.Id,
            doneAt: Day(s14Start, 3));
        var p14_t4 = Mk(tasks, "gRPC service integration layer",          13, today.AddDays(+2), cibProject.Id,
            TaskType.Feature, frank.Id, alice.Id, s14.Id, epicId: cibPayments.Id,
            requiresQa: true);
        var p14_t5 = Mk(tasks, "HIGH: Gateway request timeout regression",  5, today.AddDays(-1), cibProject.Id,
            TaskType.Bug,     grace.Id, alice.Id, s14.Id, epicId: cibPayments.Id,
            severity: BugSeverity.High,
            blocker: "Upstream payment service team SLA review required before patch");
        Mk(tasks, "Integration test automation suite",         5, s14End, cibProject.Id,
            TaskType.Test,    emma.Id,  alice.Id, s14.Id, epicId: cibApproval.Id);
        Mk(tasks, "Docker Compose environment cleanup",        2, s14End, cibProject.Id,
            TaskType.Chore,   grace.Id, alice.Id, s14.Id, epicId: cibAdmin.Id);
        var p14_qa4 = Mk(tasks, "QA: gRPC service integration layer",     3, s14End, cibProject.Id,
            TaskType.Test,    grace.Id, alice.Id, s14.Id, epicId: cibPayments.Id);

        p14_t4.SetQaTaskId(p14_qa4.Id);
        p14_qa4.SetParentTaskId(p14_t4.Id);

        // ── Product Sprint 6 (DONE, 35 pts, 27 done = 77%) ───────────────────────
        // Healthy delivery: core onboarding and ingestion features shipped; two lower-priority
        // items carried to the next sprint.
        Mk(tasks, "User onboarding wizard (3-step flow)",      8, s12End, omsProject.Id,
            TaskType.Feature, henry.Id, alice.Id, s6.Id, epicId: omsAuthRbac.Id,
            doneAt: Day(s12Start, 2));
        Mk(tasks, "MEDIUM: Profile photo upload failure",      3, s12End.AddDays(-3), omsProject.Id,
            TaskType.Bug,     henry.Id, alice.Id, s6.Id, epicId: omsAuthRbac.Id,
            severity: BugSeverity.Medium, doneAt: Day(s12Start, 1));
        Mk(tasks, "Event ingestion REST API endpoint",         8, s12End, omsProject.Id,
            TaskType.Feature, iris.Id,  alice.Id, s6.Id, epicId: omsIngestion.Id,
            doneAt: Day(s12Start, 4));
        Mk(tasks, "Payment method secure vault",               5, s12End, omsProject.Id,
            TaskType.Feature, james.Id, alice.Id, s6.Id, epicId: omsIngestion.Id,
            doneAt: Day(s12Start, 7));
        Mk(tasks, "User journey end-to-end tests",             3, s12End, omsProject.Id,
            TaskType.Test,    iris.Id,  alice.Id, s6.Id, epicId: omsAuthRbac.Id,
            doneAt: Day(s12Start, 8));
        Mk(tasks, "Mobile architecture review",                5, s12End, omsProject.Id,
            TaskType.Review,  david.Id, alice.Id, s6.Id, epicId: omsIngestion.Id);
        Mk(tasks, "Analytics SDK version upgrade",             3, s12End, omsProject.Id,
            TaskType.Chore,   james.Id, alice.Id, s6.Id, epicId: omsIngestion.Id);

        // ── Product Sprint 7 (DONE, 35 pts, 0% delivery — production crisis) ──────
        // Day 3: critical analytics crash hit production. All hands moved to incident
        // response. Sprint closed with every task still active — worst delivery on record.
        var s7_t3 = Mk(tasks, "Social auth — Google & Apple sign-in",       8, s13End, omsProject.Id,
            TaskType.Feature, james.Id, alice.Id, s7.Id, epicId: omsAuthRbac.Id,
            blocker: "Production incident: all hands on incident response");
        var s7_t4 = Mk(tasks, "CRITICAL: Analytics crash on app launch",    5, s13End.AddDays(-5), omsProject.Id,
            TaskType.Bug,     henry.Id, alice.Id, s7.Id, epicId: omsIngestion.Id,
            severity: BugSeverity.Critical);
        Mk(tasks, "AI-powered recommendation engine",         10, s13End.AddDays(-3), omsProject.Id,
            TaskType.Feature, henry.Id, alice.Id, s7.Id, epicId: omsTemplates.Id);
        Mk(tasks, "Real-time analytics pipeline",              8, s13End.AddDays(-2), omsProject.Id,
            TaskType.Feature, iris.Id,  alice.Id, s7.Id, epicId: omsIngestion.Id);
        Mk(tasks, "Recommendation engine unit tests",          4, s13End, omsProject.Id,
            TaskType.Test,    david.Id, alice.Id, s7.Id, epicId: omsTemplates.Id);

        // ── Product Sprint 8 (ACTIVE, 43 pts, 13 done — recovery sprint) ──────────
        // Opened with remediation items; critical crash fixed on day 3; analytics rebuild
        // now in QA. Henry overloaded carrying QA oversight + a new latency bug.
        Mk(tasks, "Sprint 7 post-mortem & remediation plan",  3, s14End.AddDays(-8), omsProject.Id,
            TaskType.Chore,   david.Id, alice.Id, s8.Id, epicId: omsIngestion.Id,
            doneAt: Day(s14Start, 1));
        Mk(tasks, "Revert AI recommendation deployment",       5, s14End.AddDays(-7), omsProject.Id,
            TaskType.Chore,   james.Id, alice.Id, s8.Id, epicId: omsIngestion.Id,
            doneAt: Day(s14Start, 2));
        Mk(tasks, "CRITICAL: Fix analytics crash regression",  5, s14End.AddDays(-6), omsProject.Id,
            TaskType.Bug,     henry.Id, alice.Id, s8.Id, epicId: omsIngestion.Id,
            severity: BugSeverity.Critical, doneAt: Day(s14Start, 3));
        var s8_t4 = Mk(tasks, "Rebuild analytics with stable libraries",   10, s14End, omsProject.Id,
            TaskType.Feature, henry.Id, alice.Id, s8.Id, epicId: omsIngestion.Id,
            requiresQa: true);
        var s8_t5 = Mk(tasks, "Social auth — carry-over from Sprint 7",     8, today.AddDays(-2), omsProject.Id,
            TaskType.Feature, iris.Id,  alice.Id, s8.Id, epicId: omsAuthRbac.Id);
        Mk(tasks, "Post-incident smoke test suite",             5, s14End, omsProject.Id,
            TaskType.Test,    james.Id, alice.Id, s8.Id, epicId: omsIngestion.Id);
        Mk(tasks, "Dependency security audit",                  4, s14End, omsProject.Id,
            TaskType.Chore,   david.Id, alice.Id, s8.Id, epicId: omsAuthRbac.Id);
        var s8_t8 = Mk(tasks, "MEDIUM: Dashboard data latency under load",  3, today.AddDays(+2), omsProject.Id,
            TaskType.Bug,     henry.Id, alice.Id, s8.Id, epicId: omsIngestion.Id,
            severity: BugSeverity.Medium);
        var s8_qa4 = Mk(tasks, "QA: Analytics service rebuild",             3, s14End, omsProject.Id,
            TaskType.Test,    iris.Id,  alice.Id, s8.Id, epicId: omsIngestion.Id);

        s8_t4.SetQaTaskId(s8_qa4.Id);
        s8_qa4.SetParentTaskId(s8_t4.Id);
        s8_t4.SendToQa(alice.Id);

        // ── Misc standalone tasks ─────────────────────────────────────────────────
        Mk(tasks, "Q3 roadmap consolidation",               8, today.AddDays(-2),   dashProject.Id,
            TaskType.Chore,  bob.Id,   alice.Id, null, doneAt: Day(s14Start, 0));
        Mk(tasks, "Stakeholder demo preparation",           6, today.AddDays(+7),   dashProject.Id,
            TaskType.Chore,  bob.Id,   alice.Id, null);
        Mk(tasks, "Architecture review — service mesh",     8, s14End.AddDays(+5),  apiProject.Id,
            TaskType.Review, carol.Id, alice.Id, null);
        Mk(tasks, "Q3 capacity planning review",            5, s14End.AddDays(+5),  mobileProject.Id,
            TaskType.Chore,  david.Id, alice.Id, null);

        // Core Banking — new department, no engineers yet, so its head carries the early work.
        Mk(tasks, "Draft AML transaction monitoring rules", 5, today.AddDays(+10), regReportingProject.Id,
            TaskType.Feature, priya.Id, priya.Id, null, epicId: regFilings.Id);
        Mk(tasks, "Review Basel III capital adequacy template", 3, today.AddDays(-2), regReportingProject.Id,
            TaskType.Chore,   priya.Id, priya.Id, null, epicId: regFilings.Id);

        // Infra/DevOps — same story as Core Banking: new department, no engineers yet.
        Mk(tasks, "Stand up centralized log aggregation", 5, today.AddDays(+7), platformReliabilityProject.Id,
            TaskType.Feature, felix.Id, felix.Id, null, epicId: platformObservability.Id);
        Mk(tasks, "Define SLO dashboards for core services", 3, today.AddDays(-1), platformReliabilityProject.Id,
            TaskType.Chore,   felix.Id, felix.Id, null, epicId: platformObservability.Id);

        // Assignee-fallback test case: emma is a Platform engineer and NOT an OMS member,
        // but is assigned this one OMS task. She should see THIS task (assignee fallback)
        // yet remain blocked from the rest of OMS — its other tasks and its wiki.
        Mk(tasks, "OMS — SMS spike for CIB transaction alerts", 3, today.AddDays(+5), omsProject.Id,
            TaskType.Chore,  emma.Id,  alice.Id, null);

        db.Tasks.AddRange(tasks);

        // ── 7a. Backlog tasks (CIB + OMS — no assignee, no sprint) ───────────────
        var backlog = new List<PulseTask>();

        MkBacklog(backlog, "Separate administrator login",                          cibProject.Id, cibAdmin.Id);
        MkBacklog(backlog, "Create, modify, and delete user logins",                cibProject.Id, cibAdmin.Id);
        MkBacklog(backlog, "Secure login with OTP via SMS and email",               cibProject.Id, cibAuth.Id);
        MkBacklog(backlog, "Password reset",                                        cibProject.Id, cibAuth.Id);
        MkBacklog(backlog, "Transfer funds between own accounts",                   cibProject.Id, cibPayments.Id);
        MkBacklog(backlog, "Save and manage beneficiaries",                         cibProject.Id, cibPayments.Id);
        MkBacklog(backlog, "Download bulk payment file template",                   cibProject.Id, cibBulk.Id);
        MkBacklog(backlog, "Approve or reject a bulk payment upload",               cibProject.Id, cibBulk.Id);
        MkBacklog(backlog, "Enforce single-approval flow (Initiator + Approver)",   cibProject.Id, cibApproval.Id);
        MkBacklog(backlog, "Real-time status updates across payment workflows",     cibProject.Id, cibApproval.Id);
        MkBacklog(backlog, "Set up a standing order for recurring payments",        cibProject.Id, cibStanding.Id);
        MkBacklog(backlog, "Approve a standing order before activation",            cibProject.Id, cibStanding.Id);

        MkBacklog(backlog, "Invite users with specific roles",                              omsProject.Id, omsAuthRbac.Id);
        MkBacklog(backlog, "Enable MFA to protect account",                                 omsProject.Id, omsAuthRbac.Id);
        MkBacklog(backlog, "Submit events via REST API",                                    omsProject.Id, omsIngestion.Id);
        MkBacklog(backlog, "Detect and suppress duplicate events",                          omsProject.Id, omsIngestion.Id);
        MkBacklog(backlog, "Create SMS templates with dynamic placeholders",                omsProject.Id, omsTemplates.Id);
        MkBacklog(backlog, "Review and approve templates (separation of duties)",           omsProject.Id, omsTemplates.Id);
        MkBacklog(backlog, "Configure multiple SMS providers with automatic failover",      omsProject.Id, omsSms.Id);
        MkBacklog(backlog, "View real-time SMS delivery success rate",                      omsProject.Id, omsSms.Id);
        MkBacklog(backlog, "Configure WhatsApp channel with WABA credentials",              omsProject.Id, omsWhatsApp.Id);
        MkBacklog(backlog, "Sync WhatsApp template Meta approval status automatically",     omsProject.Id, omsWhatsApp.Id);
        MkBacklog(backlog, "Upload CSV of bulk events to trigger notifications",            omsProject.Id, omsCampaigns.Id);
        MkBacklog(backlog, "View bulk upload job status and processing progress",           omsProject.Id, omsCampaigns.Id);

        db.Tasks.AddRange(backlog);

        // Assign each task's per-project display number now that every Mk/MkBacklog call across
        // the whole seed has run — cheaper and far less invasive than threading a shared counter
        // through the two task-builder helpers and every one of their many call sites above.
        var taskNumbersByProject = new Dictionary<Guid, int>();
        foreach (var t in tasks.Concat(backlog))
        {
            taskNumbersByProject.TryGetValue(t.ProjectId, out var n);
            t.AssignTaskNumber(++n);
            taskNumbersByProject[t.ProjectId] = n;
        }
        await db.SaveChangesAsync(ct);

        // ── 7b. Project members (drives individual-contributor access) ────────────
        // ICs (engineers/designers) can only access projects they are a member of.
        // The mix below is deliberate so each persona exercises a different edge:
        //   • emma  — member of CIB + API (multi-project IC); NOT an OMS member, but is
        //             assigned one OMS task above → tests the assignee fallback.
        //   • frank — member of CIB only; NOT OMS → negative control (OMS is blocked).
        //   • grace — member of CIB *and* OMS → cross-team membership (Platform eng on a
        //             Product-owned project) per the engineer-access model.
        //   • henry/iris/james — OMS (Product) members.
        //   • leo   — Mobile App v2 (Design) member.
        var members = new List<ProjectMember>
        {
            ProjectMember.Create(cibProject.Id,    emma.Id),
            ProjectMember.Create(cibProject.Id,    frank.Id),
            ProjectMember.Create(cibProject.Id,    grace.Id),
            ProjectMember.Create(apiProject.Id,    emma.Id),
            ProjectMember.Create(omsProject.Id,    henry.Id),
            ProjectMember.Create(omsProject.Id,    iris.Id),
            ProjectMember.Create(omsProject.Id,    james.Id),
            ProjectMember.Create(omsProject.Id,    grace.Id),   // cross-team
            ProjectMember.Create(mobileProject.Id, leo.Id),
        };
        db.ProjectMembers.AddRange(members);

        // ── 7c. Wiki pages (drives the cross-project wiki-index filter) ───────────
        // CIB and OMS each get pages so the /wiki index visibly differs per persona:
        // an OMS non-member (frank, or emma via task-only access) must not see OMS pages.
        var wikiPages = new List<WikiPage>
        {
            WikiPage.Create(cibProject.Id, "CIB Architecture Overview",
                "# CIB Architecture\n\nService boundaries, auth, and the payment-approval flow.", carol.Id),
            WikiPage.Create(cibProject.Id, "Auth & JWT Runbook",
                "# Auth Runbook\n\nJWT rotation, session handling, and the login-incident playbook.", alice.Id),
            WikiPage.Create(omsProject.Id, "OMS Delivery Pipeline",
                "# OMS Pipeline\n\nEvent ingestion → templating → SMS/WhatsApp delivery.", david.Id),
            WikiPage.Create(omsProject.Id, "Template Authoring Guide",
                "# Templates\n\nAuthoring, placeholders, approval (separation of duties), and versioning.", diana.Id),
        };
        db.WikiPages.AddRange(wikiPages);
        await db.SaveChangesAsync(ct);

        // ── 8. Escalation events ──────────────────────────────────────────────────
        var escalations = new List<EscalationEvent>
        {
            // Sprint 14 — gateway bug overdue; gRPC approaching tight deadline
            EscalationEvent.Record(p14_t5.Id, EscalationLevel.TMinus3),
            EscalationEvent.Record(p14_t5.Id, EscalationLevel.Overdue),
            EscalationEvent.Record(p14_t4.Id, EscalationLevel.TMinus3),
            // Sprint 8 — social auth carry-over overdue; latency bug T-3
            EscalationEvent.Record(s8_t5.Id,  EscalationLevel.TMinus3),
            EscalationEvent.Record(s8_t5.Id,  EscalationLevel.Overdue),
            EscalationEvent.Record(s8_t8.Id,  EscalationLevel.TMinus3),
            // Sprint 7 historical — escalations recorded before sprint ended
            EscalationEvent.Record(s7_t4.Id,  EscalationLevel.TMinus3),
            EscalationEvent.Record(s7_t4.Id,  EscalationLevel.Overdue),
            EscalationEvent.Record(s7_t3.Id,  EscalationLevel.TMinus3),
        };
        db.EscalationEvents.AddRange(escalations);

        // ── 9. Check-ins (last 10 weekdays per engineer) ──────────────────────────
        var checkIns = new List<CheckIn>();
        var weekdays = GetPastWeekdays(today, 10).ToList();

        var checkinData = new (Engineer eng, int skipLast, string completed, string planned, string? blocker)[]
        {
            (alice, 0, "Reviewed engineering metrics and signed off on Sprint 14 scope",            "Leadership sync and threshold review",                   null),
            (bob,   0, "Finalised Q3 roadmap alignment with product and PMO",                       "Stakeholder demo preparation for tomorrow",              null),
            (carol, 0, "Sprint 14 gateway routing merged and health dashboard shipped",             "Architecture review for service mesh",                   null),
            (david, 0, "Sprint 8 post-mortem closed and incident timeline finalised",               "Capacity planning sync for Sprint 9",                    null),
            (emma,  0, "Service health dashboard complete, integration tests started",              "Continue integration tests, support gRPC review",        null),
            (frank, 1, "gRPC integration layer 70% done, hit proto schema compatibility issue",     "Resolve proto schema, aim to wrap gRPC today",           "Proto schema backward-compatibility issue with CIB v1 contracts"),
            (grace, 2, "Investigating gateway timeout regression — hard to reproduce",              "Reproduce and isolate timeout root cause",               "Upstream payment service team unresponsive on SLA review"),
            (henry, 2, "Analytics rebuild sent to QA, monitoring for regressions",                 "Support iris on QA pass; investigate dashboard latency", "Two concurrent high-priority tasks — need triage"),
            (iris,  0, "Running QA on analytics rebuild, social auth implementation 40% done",     "Complete social auth implementation",                    null),
            (james, 0, "Post-incident reverts complete, smoke test suite 60% done",                "Finish smoke tests, start dependency audit",             null),
            (leo,   0, "Design tokens for CIB v2 UI kit drafted and reviewed with engineers",      "Continue UI kit, sync with frontend team",               null),
            (mia,   0, "Updated project tracker and sprint risk register",                         "Prepare PMO weekly status report",                       null),
        };

        foreach (var (eng, skipLast, completed, planned, blocker) in checkinData)
            foreach (var day in weekdays.Skip(skipLast))
                checkIns.Add(CheckIn.Submit(eng.Id, day, completed, planned, blocker));

        db.CheckIns.AddRange(checkIns);

        // ── 10. Vitals responses (this week + last week) ───────────────────────────
        var vitalsData = new (Engineer eng, int thisScore, string? thisComment, int? lastScore, string? lastComment)[]
        {
            (alice, 4, "Teams stabilising after S7 incident. CIB Sprint 14 looks healthy.",          5,  null),
            (bob,   4, "Demo prep is stressful but roadmap alignment is solid.",                      4,  null),
            (carol, 4, null,                                                                          4,  null),
            (david, 3, "Team morale recovering slowly after the Sprint 7 incident.",                  4,  null),
            (emma,  4, "Good sprint so far — gateway work shipped cleanly.",                          5,  "Health monitoring delivered on time."),
            (frank, 3, "Proto schema blocker is eating into gRPC delivery window.",                   4,  null),
            (grace, 2, "Stuck on gateway timeout — blocker from upstream, no ETA on response.",      3,  "Slightly stressed about the unresolved blocker."),
            (henry, 2, "Overloaded — analytics QA and dashboard latency bug running in parallel.",   3,  "Sprint 7 incident recovery has been exhausting."),
            (iris,  4, null,                                                                          4,  "Sprint 6 delivery was solid."),
            (james, 3, "Back on track post-incident. Smoke tests are going well.",                   2,  "Sprint 7 was extremely rough."),
            (leo,   4, "Design token work progressing well.",                                         null, null),
            (mia,   4, "Good sprint visibility this week.",                                           null, null),
        };

        var vitalsResponses = new List<VitalsResponse>();
        foreach (var (eng, thisScore, thisComment, lastScore, lastComment) in vitalsData)
        {
            vitalsResponses.Add(VitalsResponse.Submit(eng.Id, thisScore, thisComment, monday));
            if (lastScore.HasValue)
                vitalsResponses.Add(VitalsResponse.Submit(eng.Id, lastScore.Value, lastComment, lastMonday));
        }
        db.VitalsResponses.AddRange(vitalsResponses);

        // ── 11. Feedback ──────────────────────────────────────────────────────────
        // Department scoping:
        //   Engineering (emma, frank, grace → Platform Team) → alice sees
        //   Product     (henry, iris, james → Product Team)  → diana sees
        //   Design      (leo → Design Team)                  → charlie sees
        //   PMO         (mia → PMO Office)                   → nina sees
        // Patterns threshold (≥3 distinct sources per week):
        //   monday      = 7 sources ✓  |  lastMonday = 2 (below threshold)  |  twoWeeksAgo = 3 ✓
        var feedbackItems = new List<Domain.Feedback.Feedback>
        {
            Domain.Feedback.Feedback.Submit(emma.Id,  "The incident response process was clear and well-coordinated. The runbook saved us.",              monday,      null),
            Domain.Feedback.Feedback.Submit(frank.Id, "Need more context on proto schema compatibility requirements — this blocker cost us two days.",     monday,      p14_t4.Id),
            Domain.Feedback.Feedback.Submit(grace.Id, "The upstream SLA blocker needs a formal escalation path. Too much time waiting.",                  monday,      p14_t5.Id),
            Domain.Feedback.Feedback.Submit(henry.Id, "Running QA oversight and a new bug investigation in parallel is unsustainable.",                   monday,      null),
            Domain.Feedback.Feedback.Submit(james.Id, "Post-mortem process was thorough. The remediation plan has clear owners and deadlines.",            monday,      null),
            Domain.Feedback.Feedback.Submit(leo.Id,   "Would benefit from more regular sync with the product team on design token adoption.",             monday,      null),
            Domain.Feedback.Feedback.Submit(mia.Id,   "Sprint risk register is working well — blockers are surfacing earlier than before.",               monday,      null),
            Domain.Feedback.Feedback.Submit(iris.Id,  "Sprint 7 scope should have been cut once the analytics lib instability was flagged.",              lastMonday,  null),
            Domain.Feedback.Feedback.Submit(emma.Id,  "Gateway timeout investigation needs a dedicated slot — context-switching is slowing it down.",     lastMonday,  null),
            Domain.Feedback.Feedback.Submit(henry.Id, "Sprint 7 post-mortem was excellent — actionable outcomes and clear owners.",                       twoWeeksAgo, null),
            Domain.Feedback.Feedback.Submit(iris.Id,  "Sprint 6 was the smoothest delivery in months. Cross-team coordination on push notifications was great.", twoWeeksAgo, null),
            Domain.Feedback.Feedback.Submit(james.Id, "GDPR requirements need a dedicated legal brief — the ambiguity is adding risk to every sprint.",   twoWeeksAgo, null),
        };
        db.Feedback.AddRange(feedbackItems);

        // ── 12. Notifications ─────────────────────────────────────────────────────
        var notifs = new List<Notification>
        {
            // Escalation alerts — Sprint 14 (Platform)
            Notification.Create(grace.Id, NotificationKind.EscalationOverdue, $$"""{"taskId":"{{p14_t5.Id}}","title":"HIGH: Gateway request timeout regression"}"""),
            Notification.Create(grace.Id, NotificationKind.EscalationT3,      $$"""{"taskId":"{{p14_t5.Id}}","title":"HIGH: Gateway request timeout regression"}"""),
            Notification.Create(frank.Id, NotificationKind.EscalationT3,      $$"""{"taskId":"{{p14_t4.Id}}","title":"gRPC service integration layer"}"""),
            // Escalation alerts — Sprint 8 (Product)
            Notification.Create(iris.Id,  NotificationKind.EscalationOverdue, $$"""{"taskId":"{{s8_t5.Id}}","title":"Social auth — carry-over from Sprint 7"}"""),
            Notification.Create(henry.Id, NotificationKind.EscalationT3,      $$"""{"taskId":"{{s8_t8.Id}}","title":"MEDIUM: Dashboard data latency under load"}"""),
            // Blocker alerts — PM sees all blocked tasks
            Notification.Create(bob.Id,   NotificationKind.BlockerFlagged,    $$"""{"taskId":"{{p14_t5.Id}}","title":"HIGH: Gateway request timeout regression"}"""),
            Notification.Create(bob.Id,   NotificationKind.BlockerFlagged,    $$"""{"taskId":"{{p13_t1.Id}}","title":"Build rate limiting service"}"""),
            Notification.Create(bob.Id,   NotificationKind.BlockerFlagged,    $$"""{"taskId":"{{s7_t3.Id}}","title":"Social auth — Google & Apple sign-in"}"""),
            // Check-in reminders — engineers who skipped last 1-2 days
            Notification.Create(frank.Id, NotificationKind.CheckInReminder,   null),
            Notification.Create(grace.Id, NotificationKind.CheckInReminder,   null),
            Notification.Create(henry.Id, NotificationKind.CheckInReminder,   null),
            // Weekly report — head of R&D (already read)
            MarkRead(Notification.Create(alice.Id, NotificationKind.WeeklyReportReady,  null)),
            // Vitals prompts — read by most
            MarkRead(Notification.Create(emma.Id,  NotificationKind.WeeklyVitalsPrompt, null)),
            MarkRead(Notification.Create(frank.Id, NotificationKind.WeeklyVitalsPrompt, null)),
            MarkRead(Notification.Create(carol.Id, NotificationKind.WeeklyVitalsPrompt, null)),
            MarkRead(Notification.Create(leo.Id,   NotificationKind.WeeklyVitalsPrompt, null)),
            MarkRead(Notification.Create(mia.Id,   NotificationKind.WeeklyVitalsPrompt, null)),
        };
        db.Notifications.AddRange(notifs);

        await db.SaveChangesAsync(ct);

        return new DemoSeedSummary(
            Engineers:      engineers.Length,
            Teams:          5,
            Projects:       7,
            Epics:          13,
            Sprints:        8,
            Tasks:          tasks.Count + backlog.Count,
            CheckIns:       checkIns.Count,
            VitalsResponses: vitalsResponses.Count,
            Feedback:       feedbackItems.Count,
            Notifications:  notifs.Count,
            Members:        members.Count,
            WikiPages:      wikiPages.Count,
            DefaultPassword: DemoPassword
        );
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static PulseTask Mk(
        List<PulseTask> list,
        string title, int points, DateOnly? due,
        Guid projectId, TaskType type,
        Guid assigneeId, Guid actorId, Guid? sprintId,
        DateTime? doneAt = null, string? blocker = null,
        BugSeverity? severity = null, Guid? epicId = null,
        bool requiresQa = false)
    {
        var task = PulseTask.Create(title, points, projectId, type, due, actorId);
        task.Assign(assigneeId, actorId);
        if (sprintId.HasValue) task.AssignToSprint(sprintId.Value);
        if (epicId.HasValue)   task.AssignToEpic(epicId.Value);
        if (severity.HasValue) task.SetSeverity(severity.Value);
        if (requiresQa)        task.SetRequiresQa(true);
        if (doneAt.HasValue)   task.SeedDoneAt(actorId, doneAt.Value);
        else if (blocker != null) task.FlagBlocker(blocker, actorId);
        list.Add(task);
        return task;
    }

    private static void MkBacklog(List<PulseTask> list, string title, Guid projectId, Guid epicId)
    {
        var task = PulseTask.Create(title, 0, projectId, TaskType.Feature);
        task.AssignToEpic(epicId);
        list.Add(task);
    }

    // Returns a DateTime at 14:00 UTC on the Nth calendar day after the sprint start.
    private static DateTime Day(DateOnly sprintStart, int calendarDayOffset)
        => sprintStart.AddDays(calendarDayOffset).ToDateTime(new TimeOnly(14, 0), DateTimeKind.Utc);

    private static Notification MarkRead(Notification n) { n.MarkRead(); return n; }

    private static DateOnly GetMonday(DateOnly date)
    {
        var d = (int)date.DayOfWeek;
        var offset = d == 0 ? -6 : -(d - 1);
        return date.AddDays(offset);
    }

    private static IEnumerable<DateOnly> GetPastWeekdays(DateOnly from, int count)
    {
        var current = from;
        var yielded = 0;
        while (yielded < count)
        {
            if (current.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                yield return current;
                yielded++;
            }
            current = current.AddDays(-1);
        }
    }
}
