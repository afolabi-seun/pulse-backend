using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using FluentValidation;
using MediatR;

namespace Pulse.Application.Feedback.Commands;

public record SubmitFeedbackCommand(string Text, Guid ActorId, Guid? TaskId = null) : IRequest<ServiceResult<FeedbackDto>>;

public class SubmitFeedbackValidator : AbstractValidator<SubmitFeedbackCommand>
{
    public SubmitFeedbackValidator()
    {
        RuleFor(x => x.Text).NotEmpty().MaximumLength(5000);
    }
}

public class SubmitFeedbackHandler : IRequestHandler<SubmitFeedbackCommand, ServiceResult<FeedbackDto>>
{
    private readonly IFeedbackRepository _feedback;

    public SubmitFeedbackHandler(IFeedbackRepository feedback) => _feedback = feedback;

    public async Task<ServiceResult<FeedbackDto>> Handle(SubmitFeedbackCommand command, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Anchor to Monday of the current week
        var dayOfWeek = (int)today.DayOfWeek;
        var weekOf = today.AddDays(dayOfWeek == 0 ? -6 : 1 - dayOfWeek);

        var entry = Domain.Feedback.Feedback.Submit(command.ActorId, command.Text, weekOf, command.TaskId);
        await _feedback.AddAsync(entry, ct);
        await _feedback.SaveChangesAsync(ct);

        return ServiceResult<FeedbackDto>.Ok(FeedbackDto.From(entry));
    }
}
