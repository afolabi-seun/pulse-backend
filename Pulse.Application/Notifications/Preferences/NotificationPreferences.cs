using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Notifications.Preferences;

/// <param name="Email">Whether this kind is emailed to the person.</param>
/// <param name="EmailLocked">A security notice: email can't be switched off.</param>
/// <param name="Chat">Whether this kind is sent to the person's personal chat channel, if they chose one.</param>
public record NotificationPreferenceDto(string Kind, string Category, string Label, string Description, bool Email, bool EmailLocked, bool Chat);

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
                EmailLocked: k.Mandatory,
                Chat: !overrides.TryGetValue(k.Kind, out var c) || c.ChatEnabled))
            .ToList());
    }
}

/// <summary>Sets email and/or chat for one kind; a null leaves that setting as it is.</summary>
public record UpdateNotificationPreferenceCommand(Guid UserId, string Kind, bool? Email, bool? Chat = null) : IRequest<ServiceResult<NotificationPreferenceDto>>;

public class UpdateNotificationPreferenceHandler(INotificationPreferenceRepository preferences)
    : IRequestHandler<UpdateNotificationPreferenceCommand, ServiceResult<NotificationPreferenceDto>>
{
    public async Task<ServiceResult<NotificationPreferenceDto>> Handle(UpdateNotificationPreferenceCommand cmd, CancellationToken ct)
    {
        if (NotificationCatalog.Find(cmd.Kind) is not { } info)
            return ServiceResult<NotificationPreferenceDto>.Fail("NOT_FOUND", $"Unknown notification kind '{cmd.Kind}'.");
        if (info.Mandatory && cmd.Email == false)
            return ServiceResult<NotificationPreferenceDto>.Fail("BUSINESS_RULE_VIOLATION", "Security notices are always emailed.");

        var preference = await preferences.GetAsync(cmd.UserId, cmd.Kind, ct);
        if (preference is null)
        {
            preference = NotificationPreference.Create(cmd.UserId, cmd.Kind, cmd.Email ?? true, cmd.Chat ?? true);
            await preferences.AddAsync(preference, ct);
        }
        else
        {
            if (cmd.Email is bool email) preference.SetEmail(email);
            if (cmd.Chat is bool chat) preference.SetChat(chat);
        }
        await preferences.SaveChangesAsync(ct);

        return ServiceResult<NotificationPreferenceDto>.Ok(new NotificationPreferenceDto(info.Kind, info.Category, info.Label,
            info.Description, info.Mandatory || preference.EmailEnabled, info.Mandatory, preference.ChatEnabled));
    }
}
