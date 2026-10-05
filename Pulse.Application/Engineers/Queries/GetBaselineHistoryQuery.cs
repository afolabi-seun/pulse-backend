using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Engineers.Queries;

public record BaselineHistoryEntryDto(
    int FromPoints,
    int FromCycleDays,
    int ToPoints,
    int ToCycleDays,
    Guid ChangedBy,
    DateTime ChangedAt);

public record GetBaselineHistoryQuery(Guid EngineerId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<BaselineHistoryEntryDto>>>;

public class GetBaselineHistoryHandler
    : IRequestHandler<GetBaselineHistoryQuery, ServiceResult<IReadOnlyList<BaselineHistoryEntryDto>>>
{
    private readonly IAuditLogRepository _audit;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;

    public GetBaselineHistoryHandler(IAuditLogRepository audit, IEngineerRepository engineers, ITeamRepository teams)
    {
        _audit = audit;
        _engineers = engineers;
        _teams = teams;
    }

    public async Task<ServiceResult<IReadOnlyList<BaselineHistoryEntryDto>>> Handle(
        GetBaselineHistoryQuery query, CancellationToken ct)
    {
        // Non-PMO/PM heads may only inspect engineers in their own department.
        var deptEngineerIds = query.ActorRole is Roles.HeadOfPmo or Roles.ProjectManager or Roles.HeadOfProduct
            ? null
            : await DepartmentScope.EngineerIdsAsync(query.ActorId, _engineers, _teams, ct);
        if (deptEngineerIds is not null && !deptEngineerIds.Contains(query.EngineerId))
            return ServiceResult<IReadOnlyList<BaselineHistoryEntryDto>>.Fail("FORBIDDEN", "You do not have access to this engineer.");

        // Pull all BASELINE_CHANGED entries (limit 200 is generous; baselines rarely change more than that)
        var entries = await _audit.ListAsync(null, 200, null, "BASELINE_CHANGED", null, null, ct);

        var results = new List<BaselineHistoryEntryDto>();
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Detail)) continue;
            try
            {
                var doc = JsonDocument.Parse(e.Detail);
                var root = doc.RootElement;

                if (!root.TryGetProperty("engineerId", out var eid)) continue;
                if (!Guid.TryParse(eid.GetString(), out var engineerId)) continue;
                if (engineerId != query.EngineerId) continue;

                results.Add(new BaselineHistoryEntryDto(
                    root.GetProperty("fromPoints").GetInt32(),
                    root.GetProperty("fromCycleDays").GetInt32(),
                    root.GetProperty("toPoints").GetInt32(),
                    root.GetProperty("toCycleDays").GetInt32(),
                    e.ActorId,
                    e.Ts));
            }
            catch (JsonException) { /* skip malformed entries */ }
        }

        return ServiceResult<IReadOnlyList<BaselineHistoryEntryDto>>.Ok(results);
    }
}
