using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using MediatR;

namespace Pulse.Application.Reports;

/// <summary>The day reports and charts start from, or null when they look back as far as they like.</summary>
public record ReportingBaselineDto(DateOnly? Date);

public record GetReportingBaselineQuery : IRequest<ServiceResult<ReportingBaselineDto>>;

public class GetReportingBaselineHandler : IRequestHandler<GetReportingBaselineQuery, ServiceResult<ReportingBaselineDto>>
{
    private readonly OverworkThresholds _thresholds;

    public GetReportingBaselineHandler(OverworkThresholds thresholds) => _thresholds = thresholds;

    public Task<ServiceResult<ReportingBaselineDto>> Handle(GetReportingBaselineQuery query, CancellationToken ct) =>
        Task.FromResult(ServiceResult<ReportingBaselineDto>.Ok(new ReportingBaselineDto(_thresholds.ReportingBaseline)));
}

/// <summary>Sets (or, with a null date, removes) the reporting baseline. Records stay exactly as they are; reports just stop looking further back.</summary>
public record SetReportingBaselineCommand(DateOnly? Date, Guid ActorId, string? IpAddress) : IRequest<ServiceResult<ReportingBaselineDto>>;

public class SetReportingBaselineHandler : IRequestHandler<SetReportingBaselineCommand, ServiceResult<ReportingBaselineDto>>
{
    private readonly IThresholdRepository _repo;
    private readonly OverworkThresholds _thresholds;
    private readonly IAuditLogRepository _audit;

    public SetReportingBaselineHandler(IThresholdRepository repo, OverworkThresholds thresholds, IAuditLogRepository audit)
    {
        _repo = repo;
        _thresholds = thresholds;
        _audit = audit;
    }

    public async Task<ServiceResult<ReportingBaselineDto>> Handle(SetReportingBaselineCommand cmd, CancellationToken ct)
    {
        if (cmd.Date is { } date && date > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
            return ServiceResult<ReportingBaselineDto>.Fail("VALIDATION_ERROR", "The reporting baseline cannot be in the future.");

        var before = _thresholds.ReportingBaseline;
        _thresholds.ReportingBaseline = cmd.Date;
        await _repo.SetAsync("ReportingBaseline", cmd.Date?.ToString("yyyy-MM-dd") ?? string.Empty, ct);

        await _audit.LogAsync("REPORTING_BASELINE_SET", cmd.ActorId, cmd.IpAddress,
            $"Reporting baseline changed from {before?.ToString("yyyy-MM-dd") ?? "none"} to {cmd.Date?.ToString("yyyy-MM-dd") ?? "none"}", ct);

        return ServiceResult<ReportingBaselineDto>.Ok(new ReportingBaselineDto(cmd.Date));
    }
}
