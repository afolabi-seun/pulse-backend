using Pulse.Domain.Common;

namespace Pulse.Domain.Reports;

public class WeeklyReport : Entity
{
    public Guid TeamId { get; private set; }
    public DateOnly WeekOf { get; private set; }
    public Guid AuthorId { get; private set; }
    public string ExecutiveSummary { get; private set; } = string.Empty;
    public string KeyAccomplishments { get; private set; } = string.Empty;
    public string PlannedNextWeek { get; private set; } = string.Empty;
    public string ResourcingNotes { get; private set; } = string.Empty;
    public Guid? SubmittedById { get; private set; }
    public DateTime? SubmittedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    private WeeklyReport() { }

    public static WeeklyReport Create(Guid teamId, DateOnly weekOf, Guid authorId) =>
        new() { TeamId = teamId, WeekOf = weekOf, AuthorId = authorId };

    public void UpdateDraft(string executiveSummary, string keyAccomplishments, string plannedNextWeek, string resourcingNotes, Guid editorId)
    {
        ExecutiveSummary = executiveSummary;
        KeyAccomplishments = keyAccomplishments;
        PlannedNextWeek = plannedNextWeek;
        ResourcingNotes = resourcingNotes;
        AuthorId = editorId;
        UpdatedAt = DateTime.UtcNow;

        // Editing after sign-off re-opens the report as a draft — a submitted report must
        // always reflect what was actually signed off.
        SubmittedById = null;
        SubmittedAt = null;
    }

    public void Submit(Guid submittedById)
    {
        SubmittedById = submittedById;
        SubmittedAt = DateTime.UtcNow;
    }
}
