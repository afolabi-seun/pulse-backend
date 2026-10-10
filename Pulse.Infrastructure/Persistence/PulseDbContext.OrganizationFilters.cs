using Pulse.Domain.Alerts;
using Pulse.Domain.Automations;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Email;
using Pulse.Domain.Engineers;
using Pulse.Domain.Epics;
using Pulse.Domain.Escalations;
using Pulse.Domain.Integrations;
using Pulse.Domain.Notifications;
using Pulse.Domain.Organizations;
using Pulse.Domain.Overrides;
using Pulse.Domain.Projects;
using Pulse.Domain.Reports;
using Pulse.Domain.Sprints;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using Pulse.Domain.TimeEntries;
using Pulse.Domain.Vitals;
using Pulse.Application.Overwork;
using Microsoft.EntityFrameworkCore;
using FeedbackEntity = Pulse.Domain.Feedback.Feedback;

namespace Pulse.Infrastructure.Persistence;

/// <summary>
/// Multi-tenancy Phase 1c: every query is scoped to the caller's organization by EF Core global query
/// filters (see docs/design/multi-tenancy-and-billing.md). Tables that carry organization_id compare it
/// directly; every other table reaches an org through its parent (engineer, project, team, task, …),
/// and because a filter that queries another DbSet also gets that DbSet's own filter, the whole chain
/// stays inside one org — e.g. a comment is visible only if its task is, which is visible only if its
/// project is in the caller's org.
///
/// <see cref="CurrentOrganizationId"/> is the authenticated caller's org, or the org a recurring job is
/// running for (OrganizationJobRunner, Phase 1e). It's null — no filtering — for anonymous endpoints such
/// as login, which must find an engineer by email before any org is known, for ad-hoc background jobs not
/// run per org, and for tests' seed helpers. An authenticated caller whose token somehow lacks an org
/// fails closed (Guid.Empty matches nothing).
/// </summary>
public partial class PulseDbContext
{
    private Guid? CurrentOrganizationId =>
        _currentUser switch
        {
            null => null,
            { IsAuthenticated: true } user => user.OrganizationId ?? Guid.Empty,
            var user => user.OrganizationId, // background work run for one org (Phase 1e), else unscoped
        };

    private bool _archivedTasksVisible;

    /// <summary>True while an <see cref="IncludeArchivedTasks"/> scope is open.</summary>
    private bool ArchivedTasksVisible => _archivedTasksVisible;

    /// <summary>
    /// Lets queries run inside the scope see archived tasks. Archived tasks are hidden from every query by default (boards, lists, workload,
    /// escalations, reports); the few callers that must see them — task numbering, so a number is never reused; time-entry joins, so hours stay
    /// on their project; viewing, listing and restoring archived tasks — open this around the query. It is deliberately NOT IgnoreQueryFilters():
    /// that would also drop the organization filter and show another organization's tasks.
    /// </summary>
    public IDisposable IncludeArchivedTasks()
    {
        var previous = _archivedTasksVisible;
        _archivedTasksVisible = true;
        return new ArchivedTasksScope(() => _archivedTasksVisible = previous);
    }

    private sealed class ArchivedTasksScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampOrganizationOnNewRows();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampOrganizationOnNewRows();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// A row an authenticated caller creates belongs to the caller's organization — whatever the entity's
    /// own default (Organization.DefaultId) says — so creation is org-correct without every Create method
    /// taking an org. Unauthenticated work (background jobs, until Phase 1e) keeps the default. An
    /// authenticated caller with no org stamps Guid.Empty, which the foreign key rejects: fails closed.
    /// </summary>
    private void StampOrganizationOnNewRows()
    {
        if (CurrentOrganizationId is not Guid orgId)
            return;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added && entry.Metadata.FindProperty("OrganizationId") is not null)
                entry.Property("OrganizationId").CurrentValue = orgId;
        }
    }

    private void ApplyOrganizationFilters(ModelBuilder b)
    {
        // ── Carry organization_id directly ──────────────────────────────────────
        b.Entity<Organization>().HasQueryFilter(o => CurrentOrganizationId == null || o.Id == CurrentOrganizationId);
        b.Entity<OrganizationLogo>().HasQueryFilter(l => CurrentOrganizationId == null || l.OrganizationId == CurrentOrganizationId);
        b.Entity<Team>().HasQueryFilter(t => CurrentOrganizationId == null || t.OrganizationId == CurrentOrganizationId);
        b.Entity<Engineer>().HasQueryFilter(e => CurrentOrganizationId == null || e.OrganizationId == CurrentOrganizationId);
        b.Entity<Project>().HasQueryFilter(p => CurrentOrganizationId == null || p.OrganizationId == CurrentOrganizationId);
        b.Entity<ThresholdSetting>().HasQueryFilter(s => CurrentOrganizationId == null || s.OrganizationId == CurrentOrganizationId);
        b.Entity<DepartmentThresholdOverride>().HasQueryFilter(d => CurrentOrganizationId == null || d.OrganizationId == CurrentOrganizationId);
        // An unlinked space (null organization) is visible to no organization — only to unscoped work.
        b.Entity<GoogleChatSpace>().HasQueryFilter(s => CurrentOrganizationId == null || s.OrganizationId == CurrentOrganizationId);
        b.Entity<GoogleChatLinkCode>().HasQueryFilter(c => CurrentOrganizationId == null || c.OrganizationId == CurrentOrganizationId);
        b.Entity<FailedEmail>().HasQueryFilter(e => CurrentOrganizationId == null || e.OrganizationId == CurrentOrganizationId);
        b.Entity<SlackInstallation>().HasQueryFilter(s => CurrentOrganizationId == null || s.OrganizationId == CurrentOrganizationId);

        // ── Through an engineer ─────────────────────────────────────────────────
        b.Entity<RefreshToken>().HasQueryFilter(r => CurrentOrganizationId == null || Engineers.Any(e => e.Id == r.EngineerID));
        b.Entity<Notification>().HasQueryFilter(n => CurrentOrganizationId == null || Engineers.Any(e => e.Id == n.UserId));
        b.Entity<NotificationPreference>().HasQueryFilter(p => CurrentOrganizationId == null || Engineers.Any(e => e.Id == p.EngineerId));
        b.Entity<PersonalChatSettings>().HasQueryFilter(s => CurrentOrganizationId == null || Engineers.Any(e => e.Id == s.EngineerId));
        b.Entity<AuditLogEntry>().HasQueryFilter(a => CurrentOrganizationId == null || Engineers.Any(e => e.Id == a.ActorId));
        b.Entity<CheckIn>().HasQueryFilter(c => CurrentOrganizationId == null || Engineers.Any(e => e.Id == c.EngineerId));
        b.Entity<TimeEntry>().HasQueryFilter(t => CurrentOrganizationId == null || Engineers.Any(e => e.Id == t.EngineerId));
        b.Entity<ActiveTimer>().HasQueryFilter(t => CurrentOrganizationId == null || Engineers.Any(e => e.Id == t.EngineerId));
        b.Entity<FeedbackEntity>().HasQueryFilter(f => CurrentOrganizationId == null || Engineers.Any(e => e.Id == f.EngineerId));
        b.Entity<VitalsResponse>().HasQueryFilter(v => CurrentOrganizationId == null || Engineers.Any(e => e.Id == v.EngineerId));
        b.Entity<OverworkOverride>().HasQueryFilter(o => CurrentOrganizationId == null || Engineers.Any(e => e.Id == o.EngineerId));
        b.Entity<AlertRule>().HasQueryFilter(r => CurrentOrganizationId == null || Engineers.Any(e => e.Id == r.OwnerEngineerId));
        b.Entity<AutomationRule>().HasQueryFilter(r => CurrentOrganizationId == null || Engineers.Any(e => e.Id == r.OwnerEngineerId));

        // ── Through a project ───────────────────────────────────────────────────
        b.Entity<PulseTask>().HasQueryFilter(t => (ArchivedTasksVisible || t.ArchivedAt == null)
            && (CurrentOrganizationId == null || Projects.Any(p => p.Id == t.ProjectId)));
        b.Entity<Epic>().HasQueryFilter(e => CurrentOrganizationId == null || Projects.Any(p => p.Id == e.ProjectId));
        b.Entity<WikiPage>().HasQueryFilter(w => CurrentOrganizationId == null || Projects.Any(p => p.Id == w.ProjectId));
        b.Entity<ProjectMember>().HasQueryFilter(m => CurrentOrganizationId == null || Projects.Any(p => p.Id == m.ProjectId));
        b.Entity<ProjectFollow>().HasQueryFilter(f => CurrentOrganizationId == null || Projects.Any(p => p.Id == f.ProjectId));

        // ── Through a team ──────────────────────────────────────────────────────
        b.Entity<Sprint>().HasQueryFilter(s => CurrentOrganizationId == null || Teams.Any(t => t.Id == s.TeamId));
        b.Entity<WeeklyReport>().HasQueryFilter(r => CurrentOrganizationId == null || Teams.Any(t => t.Id == r.TeamId));

        // ── Through a task (→ project) ──────────────────────────────────────────
        b.Entity<TaskComment>().HasQueryFilter(c => CurrentOrganizationId == null || Tasks.Any(t => t.Id == c.TaskId));
        b.Entity<TaskHistory>().HasQueryFilter(h => CurrentOrganizationId == null || Tasks.Any(t => t.Id == h.TaskId));
        b.Entity<Subtask>().HasQueryFilter(s => CurrentOrganizationId == null || Tasks.Any(t => t.Id == s.TaskId));
        b.Entity<TaskDependency>().HasQueryFilter(d => CurrentOrganizationId == null || Tasks.Any(t => t.Id == d.DependentTaskId));
        b.Entity<TaskEstimationSession>().HasQueryFilter(s => CurrentOrganizationId == null || Tasks.Any(t => t.Id == s.TaskId));
        b.Entity<TaskEstimationVote>().HasQueryFilter(v => CurrentOrganizationId == null || Tasks.Any(t => t.Id == v.TaskId));
        b.Entity<EscalationEvent>().HasQueryFilter(e => CurrentOrganizationId == null || Tasks.Any(t => t.Id == e.TaskId));
        b.Entity<AutomationExecution>().HasQueryFilter(e => CurrentOrganizationId == null || Tasks.Any(t => t.Id == e.TaskId));

        // ── Through another child ───────────────────────────────────────────────
        b.Entity<WikiPageRevision>().HasQueryFilter(r => CurrentOrganizationId == null || WikiPages.Any(w => w.Id == r.WikiPageId));
        b.Entity<SprintRetrospective>().HasQueryFilter(r => CurrentOrganizationId == null || Sprints.Any(s => s.Id == r.SprintId));
        b.Entity<AlertConversation>().HasQueryFilter(c => CurrentOrganizationId == null || AlertRules.Any(r => r.Id == c.AlertRuleId));
        b.Entity<GoogleChatThread>().HasQueryFilter(t => CurrentOrganizationId == null || AlertRules.Any(r => r.Id == t.AlertRuleId));
        b.Entity<AlertMetricSnapshot>().HasQueryFilter(s => CurrentOrganizationId == null
            || (s.ScopeType == AlertScopeType.Team && Teams.Any(t => t.Id == s.ScopeId))
            || (s.ScopeType == AlertScopeType.Project && Projects.Any(p => p.Id == s.ScopeId)));
    }
}
