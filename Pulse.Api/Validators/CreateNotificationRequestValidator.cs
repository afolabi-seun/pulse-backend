using Pulse.Api.Controllers;
using Pulse.Domain.Notifications;
using FluentValidation;

namespace Pulse.Api.Validators;

public class CreateNotificationRequestValidator : AbstractValidator<NotificationsController.CreateNotificationRequest>
{
    private static readonly HashSet<string> ValidKinds =
    [
        NotificationKind.CheckInReminder,
        NotificationKind.EscalationT3,
        NotificationKind.EscalationT1,
        NotificationKind.EscalationOverdue,
        NotificationKind.BlockerFlagged,
        NotificationKind.WeeklyVitalsPrompt,
        NotificationKind.WeeklyReportReady,
        NotificationKind.PasswordReset,
        NotificationKind.AccountLocked,
    ];

    private static readonly HashSet<string> ValidChannels =
    [
        NotificationChannel.InApp,
        NotificationChannel.Email,
    ];

    public CreateNotificationRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Kind)
            .NotEmpty()
            .Must(k => ValidKinds.Contains(k))
            .WithMessage($"Kind must be one of: {string.Join(", ", ValidKinds)}.");
        RuleFor(x => x.Channel)
            .NotEmpty()
            .Must(c => ValidChannels.Contains(c))
            .WithMessage($"Channel must be '{NotificationChannel.InApp}' or '{NotificationChannel.Email}'.");
    }
}
