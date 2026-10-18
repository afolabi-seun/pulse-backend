using Pulse.Application.Overwork;
using Pulse.Domain.Alerts;
using Pulse.Domain.Automations;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Email;
using Pulse.Domain.Engineers;
using Pulse.Domain.Epics;
using Pulse.Domain.Escalations;
using Pulse.Domain.Feedback;
using Pulse.Domain.Notifications;
using Pulse.Domain.Overrides;
using Pulse.Domain.Integrations;
using Pulse.Domain.Organizations;
using Pulse.Domain.Projects;
using Pulse.Domain.Vitals;
using Pulse.Domain.Reports;
using Pulse.Domain.Tasks;
using Pulse.Domain.Sprints;
using Pulse.Domain.Teams;
using Pulse.Domain.TimeEntries;
using Pulse.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.Infrastructure.Persistence;

public partial class PulseDbContext : DbContext
{
    private readonly ICurrentUserService? _currentUser;

    /// <summary>Unscoped: no organization filter applies. For design-time tooling and tests that
    /// build a context directly; the app always goes through the constructor below.</summary>
    public PulseDbContext(DbContextOptions<PulseDbContext> options) : base(options) { }

    [ActivatorUtilitiesConstructor]
    public PulseDbContext(DbContextOptions<PulseDbContext> options, ICurrentUserService currentUser) : base(options) =>
        _currentUser = currentUser;

    public DbSet<Epic> Epics => Set<Epic>();
    public DbSet<PulseTask> Tasks => Set<PulseTask>();
    public DbSet<TaskHistory> TaskHistory => Set<TaskHistory>();
    public DbSet<Engineer> Engineers => Set<Engineer>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<CheckIn> CheckIns => Set<CheckIn>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<ActiveTimer> ActiveTimers => Set<ActiveTimer>();
    public DbSet<EscalationEvent> EscalationEvents => Set<EscalationEvent>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<VitalsResponse> VitalsResponses => Set<VitalsResponse>();
    public DbSet<OverworkOverride> OverworkOverrides => Set<OverworkOverride>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<PersonalChatSettings> PersonalChatSettings => Set<PersonalChatSettings>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<ThresholdSetting> ThresholdSettings => Set<ThresholdSetting>();
    public DbSet<DepartmentThresholdOverride> DepartmentThresholdOverrides => Set<DepartmentThresholdOverride>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationLogo> OrganizationLogos => Set<OrganizationLogo>();
    public DbSet<SlackInstallation> SlackInstallations => Set<SlackInstallation>();
    public DbSet<Sprint> Sprints => Set<Sprint>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<Subtask> Subtasks => Set<Subtask>();
    public DbSet<TaskEstimationSession> EstimationSessions => Set<TaskEstimationSession>();
    public DbSet<TaskEstimationVote> EstimationVotes => Set<TaskEstimationVote>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<SprintRetrospective> SprintRetrospectives => Set<SprintRetrospective>();
    public DbSet<ProjectFollow> ProjectFollows => Set<ProjectFollow>();
    public DbSet<WikiPage> WikiPages => Set<WikiPage>();
    public DbSet<WikiPageRevision> WikiPageRevisions => Set<WikiPageRevision>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<FailedEmail> FailedEmails => Set<FailedEmail>();
    public DbSet<WeeklyReport> WeeklyReports => Set<WeeklyReport>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<AlertConversation> AlertConversations => Set<AlertConversation>();
    public DbSet<AlertMetricSnapshot> AlertMetricSnapshots => Set<AlertMetricSnapshot>();
    public DbSet<GoogleChatSpace> GoogleChatSpaces => Set<GoogleChatSpace>();
    public DbSet<GoogleChatThread> GoogleChatThreads => Set<GoogleChatThread>();
    public DbSet<GoogleChatLinkCode> GoogleChatLinkCodes => Set<GoogleChatLinkCode>();
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<AutomationExecution> AutomationExecutions => Set<AutomationExecution>();
    public DbSet<TaskProgressCredit> TaskProgressCredits => Set<TaskProgressCredit>();

    /// <summary>
    /// Keeps each task's earned points equal to the sum of its dated credits, whichever command changed it. Whenever a task's status, points or manual
    /// progress changed, or one of its checklist items did, the progress it should now show is worked out (see TaskProgress), compared with what it had
    /// already earned, and the difference is written as a credit to the engineer the finished task would be credited to. Forward progress is a positive
    /// credit; a QA rejection, a return to the Backlog or a smaller estimate is a negative one. Doing it here, once, means no command can forget to.
    /// QA review tasks are left out: their points are the original task's, already counted there.
    /// </summary>
    private async Task RecordProgressCreditsAsync(CancellationToken ct)
    {
        ChangeTracker.DetectChanges();

        // Archived tasks must be visible to the lookups below (a checklist item on an archived task still changes that task's progress); the
        // organization filter stays in force, which IgnoreQueryFilters() would drop.
        using var _ = IncludeArchivedTasks();

        var changed = new HashSet<Guid>();
        foreach (var entry in ChangeTracker.Entries<PulseTask>())
        {
            if (entry.State == EntityState.Added
                || (entry.State == EntityState.Modified
                    && (entry.Property(t => t.Status).IsModified || entry.Property(t => t.Points).IsModified || entry.Property(t => t.ProgressPercent).IsModified)))
                changed.Add(entry.Entity.Id);
        }
        foreach (var entry in ChangeTracker.Entries<Subtask>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                changed.Add(entry.Entity.TaskId);
        }
        if (changed.Count == 0) return;

        // Tasks already being tracked, plus any that only had a checklist item change (archived ones included: the filter must not hide them here).
        var tasks = ChangeTracker.Entries<PulseTask>().Where(e => changed.Contains(e.Entity.Id)).Select(e => e.Entity).ToList();
        var missing = changed.Except(tasks.Select(t => t.Id)).ToList();
        if (missing.Count > 0)
            tasks.AddRange(await Tasks.Where(t => missing.Contains(t.Id)).ToListAsync(ct));

        // Checklist items as they will be once this save lands: stored ones (the tracked copy wins, so edits show), plus new ones, minus removed ones.
        var stored = await Subtasks.Where(s => changed.Contains(s.TaskId)).ToListAsync(ct);
        var removed = ChangeTracker.Entries<Subtask>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity.Id).ToHashSet();
        var added = ChangeTracker.Entries<Subtask>().Where(e => e.State == EntityState.Added && changed.Contains(e.Entity.TaskId)).Select(e => e.Entity);
        var subtasksByTask = stored.Concat(added).Where(s => !removed.Contains(s.Id)).GroupBy(s => s.TaskId)
            .ToDictionary(g => g.Key, g => (Done: g.Count(s => s.IsDone), Total: g.Count()));

        foreach (var task in tasks)
        {
            if (task.ParentTaskId.HasValue) continue;

            var (done, total) = subtasksByTask.GetValueOrDefault(task.Id);
            var effective = TaskProgress.Effective(task.Status, task.ProgressPercent, done, total);
            var earned = TaskProgress.EarnedAt(task.Points, effective);
            var delta = earned - task.EarnedPoints;
            if (delta == 0) continue;

            TaskProgressCredits.Add(TaskProgressCredit.Create(task.Id, task.CreditedEngineer, delta, effective));
            task.SetEarnedPoints(earned);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PulseDbContext).Assembly);
        ApplyOrganizationFilters(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
}
