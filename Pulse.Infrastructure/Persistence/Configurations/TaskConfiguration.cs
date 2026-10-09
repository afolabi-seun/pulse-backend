using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class TaskConfiguration : IEntityTypeConfiguration<PulseTask>
{
    public void Configure(EntityTypeBuilder<PulseTask> builder)
    {
        builder.ToTable("tasks");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.Title).HasColumnName("title").HasMaxLength(500).IsRequired();
        builder.Property(t => t.Description).HasColumnName("description");
        builder.Property(t => t.AcceptanceCriteria).HasColumnName("acceptance_criteria");
        builder.Property(t => t.Severity).HasColumnName("severity")
            .HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Priority).HasColumnName("priority");
        builder.Property(t => t.Points).HasColumnName("points").IsRequired();
        builder.Property(t => t.DueDate).HasColumnName("due_date");
        builder.Property(t => t.ActualEndDate).HasColumnName("actual_end_date");
        builder.Property(t => t.SentToQaAt).HasColumnName("sent_to_qa_at");
        builder.Property(t => t.BackendAssigneeId).HasColumnName("backend_assignee_id");
        builder.Property(t => t.Status).HasColumnName("status")
            .HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.Type).HasColumnName("type")
            .HasConversion<string>().HasMaxLength(20).IsRequired()
            .ValueGeneratedNever();
        builder.Property(t => t.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(t => t.EpicId).HasColumnName("epic_id");
        builder.Property(t => t.AssigneeId).HasColumnName("assignee_id");
        builder.Property(t => t.CreatedById).HasColumnName("created_by_id");
        builder.Property(t => t.SprintId).HasColumnName("sprint_id");
        builder.Property(t => t.BlockerReason).HasColumnName("blocker_reason");
        builder.Property(t => t.RequiresQa).HasColumnName("requires_qa").IsRequired().HasDefaultValue(false);
        builder.Property(t => t.Discipline).HasColumnName("discipline")
            .HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.RequiresFrontendHandoff).HasColumnName("requires_frontend_handoff").IsRequired().HasDefaultValue(false);
        builder.Property(t => t.CurrentStage).HasColumnName("current_stage")
            .HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.ParentTaskId).HasColumnName("parent_task_id");
        builder.Property(t => t.QaTaskId).HasColumnName("qa_task_id");
        builder.Property(t => t.ArchivedAt).HasColumnName("archived_at");
        builder.Property(t => t.ArchivedById).HasColumnName("archived_by_id");
        builder.Property(t => t.ArchiveReason).HasColumnName("archive_reason").HasMaxLength(500);
        builder.HasIndex(t => t.ArchivedAt).HasDatabaseName("ix_tasks_archived_at");
        builder.Ignore(t => t.IsArchived);

        // The "archived tasks are hidden" rule lives in PulseDbContext.ApplyOrganizationFilters, combined with the organization filter: a second
        // HasQueryFilter here would silently replace that one.
        builder.Property(t => t.ReactivationReason).HasColumnName("reactivation_reason");
        builder.Property(t => t.ReactivatedByEngineerId).HasColumnName("reactivated_by_engineer_id");
        builder.Property(t => t.PendingRejectionReason).HasColumnName("pending_rejection_reason");
        builder.Property(t => t.PendingRejectionActorId).HasColumnName("pending_rejection_actor_id");
        builder.Property(t => t.PendingRejectionResponse).HasColumnName("pending_rejection_response");
        builder.Property(t => t.PendingRejectionRespondedByEngineerId).HasColumnName("pending_rejection_responded_by_engineer_id");
        builder.Property(t => t.PendingRejectionTargetStage).HasColumnName("pending_rejection_target_stage")
            .HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.RequiresPrApproval).HasColumnName("requires_pr_approval").IsRequired().HasDefaultValue(false);
        builder.Property(t => t.PrLink).HasColumnName("pr_link");
        builder.Property(t => t.PendingPrApprovalRequestedAt).HasColumnName("pending_pr_approval_requested_at");
        builder.Property(t => t.PendingPrApprovalRequestedByEngineerId).HasColumnName("pending_pr_approval_requested_by_engineer_id");
        builder.Property(t => t.PendingPrApprovalDelegatedToEngineerId).HasColumnName("pending_pr_approval_delegated_to_engineer_id");
        builder.Property(t => t.PrApprovedAt).HasColumnName("pr_approved_at");
        builder.Property(t => t.PauseNote).HasColumnName("pause_note");
        builder.Property(t => t.PausedByProject).HasColumnName("paused_by_project").IsRequired().HasDefaultValue(false);
        builder.Property(t => t.StatusBeforePause).HasColumnName("status_before_pause")
            .HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.PausedAt).HasColumnName("paused_at");
        builder.Property(t => t.LoanedFromEngineerId).HasColumnName("loaned_from_engineer_id");
        builder.Property(t => t.ActivatedAt).HasColumnName("activated_at").IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(t => t.TaskNumber).HasColumnName("task_number").IsRequired();
        builder.Property(t => t.ExternalReference).HasColumnName("external_reference").HasMaxLength(200);

        builder.HasIndex(t => new { t.AssigneeId, t.Status }).HasDatabaseName("ix_tasks_assignee_id_status");
        builder.HasIndex(t => new { t.ProjectId, t.Status }).HasDatabaseName("ix_tasks_project_id_status");
        builder.HasIndex(t => t.DueDate).HasDatabaseName("ix_tasks_due_date");
        builder.HasIndex(t => new { t.ProjectId, t.TaskNumber }).IsUnique().HasDatabaseName("ux_tasks_project_id_task_number");

        builder.HasMany(t => t.History).WithOne().HasForeignKey("TaskId").OnDelete(DeleteBehavior.Cascade);
    }
}
