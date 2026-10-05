using FeedbackEntity = Pulse.Domain.Feedback.Feedback;

namespace Pulse.Application.Common.Interfaces;

public interface IFeedbackRepository
{
    Task<IReadOnlyList<FeedbackEntity>> ListAsync(DateOnly? weekOf, CancellationToken ct = default);
    Task<IReadOnlyList<FeedbackEntity>> ListByDepartmentAsync(string department, DateOnly? weekOf, CancellationToken ct = default);
    Task<IReadOnlyList<FeedbackEntity>> GetByWeekAsync(DateOnly weekOf, CancellationToken ct = default);
    Task<int> CountDistinctSourcesAsync(DateOnly weekOf, CancellationToken ct = default);
    Task<IReadOnlyList<(DateOnly WeekOf, int TotalCount, int DistinctSources)>> GetWeekSummariesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(DateOnly WeekOf, int TotalCount, int DistinctSources)>> GetWeekSummariesByDepartmentAsync(string department, CancellationToken ct = default);
    Task<FeedbackEntity?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(FeedbackEntity feedback, CancellationToken ct = default);
    Task DeleteAsync(FeedbackEntity feedback, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
