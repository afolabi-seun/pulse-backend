using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Users.Commands;

public record CreateUserCommand(
    string Name,
    string Email,
    string Role,
    int BaselinePoints,
    int BaselineCycleDays,
    Guid? TeamId,
    bool IsQa,
    Discipline? Discipline,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<UserDto>>;

/// <summary>
/// Creates a new user account with a temporary random password, then emails a mandatory
/// password-reset link so the user sets their own password on first sign-in.
/// Any department head can call this.
/// </summary>
public class CreateUserHandler : IRequestHandler<CreateUserCommand, ServiceResult<UserDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService _jwt;
    private readonly IEmailQueue _emailQueue;
    private readonly IAuditLogRepository _audit;
    private readonly IAppSettings _settings;

    public CreateUserHandler(
        IEngineerRepository engineers,
        ITeamRepository teams,
        IPasswordHasher hasher,
        IJwtService jwt,
        IEmailQueue emailQueue,
        IAuditLogRepository audit,
        IAppSettings settings)
    {
        _engineers = engineers;
        _teams = teams;
        _hasher = hasher;
        _jwt = jwt;
        _emailQueue = emailQueue;
        _audit = audit;
        _settings = settings;
    }

    public async Task<ServiceResult<UserDto>> Handle(CreateUserCommand cmd, CancellationToken ct)
    {
        var existing = await _engineers.GetByEmailAsync(cmd.Email, ct);
        if (existing is not null)
            return ServiceResult<UserDto>.Fail("CONFLICT", $"A user with email '{cmd.Email}' already exists.");

        // Random temporary password — user must reset on first login via the emailed link
        var passwordHash = ActivationInvite.UnusablePasswordHash(_hasher);

        var engineer = Engineer.Create(cmd.Name, cmd.Email, passwordHash, cmd.Role,
            cmd.BaselinePoints, cmd.BaselineCycleDays);

        if (cmd.TeamId is Guid teamId)
        {
            var team = await _teams.GetByIdAsync(teamId, ct);
            if (team is not null)
            {
                engineer.SetTeam(team.Name);
                engineer.AssignToTeam(team.Id);
            }
        }

        engineer.SetIsQa(cmd.IsQa);
        if (cmd.Discipline.HasValue)
            engineer.SetDiscipline(cmd.Discipline.Value);

        // Immediately set a reset token so the welcome email prompts a password set
        var rawToken = ActivationInvite.IssueToken(engineer, _jwt, _settings);

        await _engineers.AddAsync(engineer, ct);
        try
        {
            await _engineers.SaveChangesAsync(ct);
        }
        catch (DuplicateEmailException)
        {
            // The check above only sees the caller's own organization; the email belongs to someone in another.
            return ServiceResult<UserDto>.Fail("CONFLICT", $"A user with email '{cmd.Email}' already exists.");
        }

        ActivationInvite.Send(_emailQueue, _settings, cmd.Name, cmd.Email, rawToken);

        await _audit.LogAsync("USER_CREATED", cmd.ActorId, cmd.IpAddress,
            $"Created user {engineer.Id} ({cmd.Email}) with role {cmd.Role}", ct);

        return ServiceResult<UserDto>.Ok(UserDto.From(engineer));
    }
}
