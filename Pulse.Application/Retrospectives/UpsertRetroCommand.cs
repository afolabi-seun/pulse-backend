using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Sprints;
using MediatR;

namespace Pulse.Application.Retrospectives;

public record UpsertRetroCommand(
    Guid SprintId,
    Guid ActorId,
    string WentWell,
    string NeedsImprovement,
    string ActionItems) : IRequest<ServiceResult<RetroDto>>;

public class UpsertRetroHandler : IRequestHandler<UpsertRetroCommand, ServiceResult<RetroDto>>
{
    private readonly IRetroRepository _retros;
    private readonly ISprintRepository _sprints;

    public UpsertRetroHandler(IRetroRepository retros, ISprintRepository sprints)
    {
        _retros  = retros;
        _sprints = sprints;
    }

    public async Task<ServiceResult<RetroDto>> Handle(UpsertRetroCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.WentWell) &&
            string.IsNullOrWhiteSpace(command.NeedsImprovement) &&
            string.IsNullOrWhiteSpace(command.ActionItems))
            return ServiceResult<RetroDto>.Fail("VALIDATION_ERROR", "At least one section must have content.");

        var sprint = await _sprints.GetByIdAsync(command.SprintId, ct);
        if (sprint is null)
            return ServiceResult<RetroDto>.Fail("NOT_FOUND", $"Sprint '{command.SprintId}' not found.");

        var existing = await _retros.GetBySprintAsync(command.SprintId, ct);
        if (existing is null)
        {
            existing = SprintRetrospective.Create(
                command.SprintId, command.ActorId,
                command.WentWell, command.NeedsImprovement, command.ActionItems);
            await _retros.AddAsync(existing, ct);
        }
        else
        {
            existing.Update(command.WentWell, command.NeedsImprovement, command.ActionItems);
        }

        await _retros.SaveChangesAsync(ct);
        return ServiceResult<RetroDto>.Ok(RetroDto.From(existing));
    }
}
