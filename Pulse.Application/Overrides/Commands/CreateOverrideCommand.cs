using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Overrides;
using FluentValidation;
using MediatR;

namespace Pulse.Application.Overrides.Commands;

public record CreateOverrideCommand(
    Guid EngineerId,
    string Reason,
    DateTime? ExpiresAt,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<OverrideDto>>;

public class CreateOverrideValidator : AbstractValidator<CreateOverrideCommand>
{
    public CreateOverrideValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.ExpiresAt)
            .GreaterThan(DateTime.UtcNow)
            .When(x => x.ExpiresAt.HasValue)
            .WithMessage("ExpiresAt must be in the future.");
    }
}

public class CreateOverrideHandler : IRequestHandler<CreateOverrideCommand, ServiceResult<OverrideDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly IOverworkOverrideRepository _overrides;
    private readonly IAuditLogRepository _auditLog;

    public CreateOverrideHandler(
        IEngineerRepository engineers,
        IOverworkOverrideRepository overrides,
        IAuditLogRepository auditLog)
    {
        _engineers = engineers;
        _overrides = overrides;
        _auditLog = auditLog;
    }

    public async Task<ServiceResult<OverrideDto>> Handle(CreateOverrideCommand command, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(command.EngineerId, ct);
        if (engineer is null)
            return ServiceResult<OverrideDto>.Fail("NOT_FOUND", $"Engineer '{command.EngineerId}' not found.");

        var expiresAt = command.ExpiresAt ?? DateTime.UtcNow.AddDays(7);
        var @override = OverworkOverride.Grant(command.EngineerId, command.Reason, expiresAt, command.ActorId);

        await _overrides.AddAsync(@override, ct);
        await _overrides.SaveChangesAsync(ct);

        await _auditLog.LogAsync("OVERRIDE_GRANTED", command.ActorId, command.IpAddress,
            $"Override granted to engineer '{command.EngineerId}' until {expiresAt:O}", ct);

        return ServiceResult<OverrideDto>.Ok(OverrideDto.From(@override));
    }
}
