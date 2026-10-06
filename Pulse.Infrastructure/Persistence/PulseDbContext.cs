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
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<ThresholdSetting> ThresholdSettings => Set<ThresholdSetting>();
    public DbSet<DepartmentThresholdOverride> DepartmentThresholdOverrides => Set<DepartmentThresholdOverride>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Organization> Organizations => Set<Organization>();
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PulseDbContext).Assembly);
        ApplyOrganizationFilters(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
}
