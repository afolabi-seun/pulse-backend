namespace Pulse.Application.Feedback;

public record FeedbackDto(
    Guid Id, Guid EngineerId, string Text, DateOnly WeekOf, DateTime CreatedAt, Guid? TaskId,
    string? ReplyText = null, string? RepliedByName = null, DateTime? RepliedAt = null)
{
    public static FeedbackDto From(Domain.Feedback.Feedback f, string? repliedByName = null) =>
        new(f.Id, f.EngineerId, f.Text, f.WeekOf, f.CreatedAt, f.TaskId, f.ReplyText, repliedByName, f.RepliedAt);
}

public record FeedbackPatternDto(DateOnly WeekOf, int TotalResponses, int DistinctSources);

/// <param name="Weeks">Weeks with enough distinct people to show — newest first.</param>
/// <param name="HiddenWeeks">Weeks that had feedback but too few people to show without exposing anyone.</param>
/// <param name="EligiblePeople">Who in this scope could have given feedback, for a participation rate.</param>
/// <param name="Scope">"Organisation", or the department the reader is limited to.</param>
public record FeedbackPatternsDto(IReadOnlyList<FeedbackPatternDto> Weeks, int HiddenWeeks, int EligiblePeople, string Scope);
