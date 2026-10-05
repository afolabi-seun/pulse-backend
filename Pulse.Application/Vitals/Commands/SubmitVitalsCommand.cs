using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using FluentValidation;
using MediatR;

namespace Pulse.Application.Vitals.Commands;

public record SubmitVitalsCommand(int Score, string? Comment, Guid ActorId) : IRequest<ServiceResult<VitalsDto>>;

public class SubmitVitalsValidator : AbstractValidator<SubmitVitalsCommand>
{
    public SubmitVitalsValidator()
    {
        RuleFor(x => x.Score).InclusiveBetween(1, 5);
        RuleFor(x => x.Comment).MaximumLength(1000).When(x => x.Comment is not null);
    }
}

public class SubmitVitalsHandler : IRequestHandler<SubmitVitalsCommand, ServiceResult<VitalsDto>>
{
    private readonly IVitalsRepository _vitals;

    public SubmitVitalsHandler(IVitalsRepository vitals) => _vitals = vitals;

    public async Task<ServiceResult<VitalsDto>> Handle(SubmitVitalsCommand command, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dayOfWeek = (int)today.DayOfWeek;
        var weekOf = today.AddDays(dayOfWeek == 0 ? -6 : 1 - dayOfWeek);

        var existing = await _vitals.GetByEngineerAndWeekAsync(command.ActorId, weekOf, ct);
        if (existing is not null)
            return ServiceResult<VitalsDto>.Fail("CONFLICT", "Pulse already submitted for this week.");

        var response = Domain.Vitals.VitalsResponse.Submit(command.ActorId, command.Score, command.Comment, weekOf);
        await _vitals.AddAsync(response, ct);
        await _vitals.SaveChangesAsync(ct);

        return ServiceResult<VitalsDto>.Ok(VitalsDto.From(response));
    }
}
