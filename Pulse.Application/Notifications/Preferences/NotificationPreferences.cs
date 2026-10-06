using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Notifications.Preferences;

/// <param name="Email">Whether this kind is emailed to the person.</param>
/// <param name="EmailLocked">A security notice: email can't be switched off.</param>
public record NotificationPreferenceDto(string Kind, string Category, string Label, string Description, bool Email, bool EmailLocked);

public record GetNotificationPreferencesQuery(Guid UserId) : IRequest<ServiceResult<IReadOnlyList<NotificationPreferenceDto>>>;

public class GetNotificationPreferencesHandler(INotificationPreferenceRepository preferences)
    : IRequestHandler<GetNotificationPreferencesQuery, ServiceResult<IReadOnlyList<NotificationPreferenceDto>>>
{
    public async Task<ServiceResult<IReadOnlyList<NotificationPreferenceDto>>> Handle(GetNotificationPreferencesQuery query, CancellationToken ct)
    {
        var overrides = (await preferences.ListForEngineerAsync(query.UserId, ct)).ToDictionary(p => p.Kind);
        return ServiceResult<IReadOnlyList<NotificationPreferenceDto>>.Ok(NotificationCatalog.All
            .Select(k => new NotificationPreferenceDto(k.Kind, k.Category, k.Label, k.Description,
                Email: k.Mandatory || !overrides.TryGetValue(k.Kind, out var o) || o.EmailEnabled,
                EmailLocked: k.Mandatory))
            .ToList());
    }
}

public record UpdateNotificationPreferenceCommand(Guid UserId, string Kind, bool Email) : IRequest<ServiceResult<NotificationPreferenceDto>>;

public class UpdateNotificationPreferenceHandler(INotificationPreferenceRepository preferences)
    : IRequestHandler<UpdateNotificationPreferenceCommand, ServiceResult<NotificationPreferenceDto>>
{
    public async Task<ServiceResult<NotificationPreferenceDto>> Handle(UpdateNotificationPreferenceCommand cmd, CancellationToken ct)
    {
        if (NotificationCatalog.Find(cmd.Kind) is not { } info)
            return ServiceResult<NotificationPreferenceDto>.Fail("NOT_FOUND", $"Unknown notification kind '{cmd.Kind}'.");
        if (info.Mandatory && !cmd.Email)
            return ServiceResult<NotificationPreferenceDto>.Fail("BUSINESS_RULE_VIOLATION", "Security notices are always emailed.");

        var existing = await preferences.GetAsync(cmd.UserId, cmd.Kind, ct);
        if (existing is null)
            await preferences.AddAsync(NotificationPreference.Create(cmd.UserId, cmd.Kind, cmd.Email), ct);
        else
            existing.SetEmail(cmd.Email);
        await preferences.SaveChangesAsync(ct);

        return ServiceResult<NotificationPreferenceDto>.Ok(
            new NotificationPreferenceDto(info.Kind, info.Category, info.Label, info.Description, cmd.Email, info.Mandatory));
    }
}
