using System.Security.Cryptography;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Teams;
using MediatR;

namespace Pulse.Application.Users.Commands;

public record ImportUserRow(
    string Name,
    string Email,
    string Role,
    string? Team,
    int? BaselinePoints,
    int? BaselineCycleDays);

public record ImportUsersCommand(
    IReadOnlyList<ImportUserRow> Rows,
    Guid ActorId,
    string? IpAddress,
    string CallerRole) : IRequest<ServiceResult<ImportResult>>;

public class ImportUsersHandler : IRequestHandler<ImportUsersCommand, ServiceResult<ImportResult>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService _jwt;
    private readonly IEmailQueue _emailQueue;
    private readonly IAuditLogRepository _audit;
    private readonly IAppSettings _settings;

    public ImportUsersHandler(
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

    public async Task<ServiceResult<ImportResult>> Handle(ImportUsersCommand cmd, CancellationToken ct)
    {
        if (cmd.Rows.Count == 0)
            return ServiceResult<ImportResult>.Fail("BUSINESS_RULE_VIOLATION", "No rows to import.");

        if (cmd.Rows.Count > 200)
            return ServiceResult<ImportResult>.Fail("BUSINESS_RULE_VIOLATION", "Cannot import more than 200 users at once.");

        var existingEmails = (await _engineers.ListAllAsync(ct))
            .Select(e => e.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Build team cache: name → id (auto-create unknown teams)
        var teamMap = (await _teams.ListAllAsync(ct))
            .ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);

        var failures = new List<ImportRowFailure>();
        var toCreate = new List<(Engineer Engineer, string RawToken)>();
        var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < cmd.Rows.Count; i++)
        {
            var row = cmd.Rows[i];
            var rowNum = i + 2;

            if (string.IsNullOrWhiteSpace(row.Name))
            {
                failures.Add(new ImportRowFailure(rowNum, "Name is required."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Email) || !row.Email.Contains('@'))
            {
                failures.Add(new ImportRowFailure(rowNum, $"'{row.Email}' is not a valid email address."));
                continue;
            }

            var email = row.Email.Trim().ToLowerInvariant();

            if (!Roles.All.Contains(row.Role))
            {
                failures.Add(new ImportRowFailure(rowNum, $"Unknown role '{row.Role}'. Valid roles: {string.Join(", ", Roles.All)}."));
                continue;
            }

            var isPmo = Roles.UserManagementGlobalRoles.Contains(cmd.CallerRole);
            if (!isPmo)
            {
                var allowed = Roles.CreatableByDeptHead(cmd.CallerRole);
                if (!allowed.Contains(row.Role))
                {
                    failures.Add(new ImportRowFailure(rowNum, $"Role '{row.Role}' is outside your department — skipped."));
                    continue;
                }
            }

            if (existingEmails.Contains(email) || seenEmails.Contains(email))
            {
                failures.Add(new ImportRowFailure(rowNum, $"'{email}' already exists — skipped."));
                continue;
            }

            var tempPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var passwordHash = _hasher.Hash(tempPassword);

            var engineer = Engineer.Create(
                row.Name.Trim(),
                email,
                passwordHash,
                row.Role,
                row.BaselinePoints ?? 10,
                row.BaselineCycleDays ?? 14);

            // Team is required — except for Executive/HR/Accountant, the roles explicitly designed
            // to have no team (org-wide, read-only stakeholders, not members of any team). Matches
            // CreateUserRequestValidator's single-user-create rule.
            if (row.Role is not (Roles.Executive or Roles.HR or Roles.Accountant) && string.IsNullOrWhiteSpace(row.Team))
            {
                failures.Add(new ImportRowFailure(rowNum, "Team is required."));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(row.Team))
            {
                var tname = row.Team.Trim();
                engineer.SetTeam(tname);

                if (!teamMap.TryGetValue(tname, out var teamId))
                {
                    // Auto-create team; default department to team name
                    var newTeam = Team.Create(tname, null, tname);
                    await _teams.AddAsync(newTeam, ct);
                    await _teams.SaveChangesAsync(ct);
                    teamId = newTeam.Id;
                    teamMap[tname] = teamId;
                }

                engineer.AssignToTeam(teamId);
            }

            var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var tokenHash = _jwt.HashToken(rawToken);
            engineer.SetPasswordResetToken(tokenHash, DateTime.UtcNow.AddDays(_settings.ActivationTokenExpiryDays));

            await _engineers.AddAsync(engineer, ct);
            seenEmails.Add(email);
            toCreate.Add((engineer, rawToken));
        }

        if (toCreate.Count == 0)
            return ServiceResult<ImportResult>.Ok(new ImportResult(0, failures));

        await _engineers.SaveChangesAsync(ct);

        foreach (var (engineer, rawToken) in toCreate)
        {
            var activationLink = $"{_settings.AppBaseUrl}/reset-password?token={Uri.EscapeDataString(rawToken)}";
            var body = $"""
                <p>Hi {engineer.Name},</p>
                <p>You've been invited to <strong>Pulse</strong> — an engineering team management platform that helps track tasks, sprints, check-ins, and team capacity.</p>
                <p>Click the button below to set your password and access your account:</p>
                {EmailTemplate.Button(activationLink, "Accept invitation")}
                {EmailTemplate.Muted($"This link expires in {_settings.ActivationTokenExpiryDays} day(s). If you were not expecting this invitation, you can safely ignore this email.")}
                """;
            _emailQueue.Enqueue(engineer.Email, "You've been invited to Pulse", EmailTemplate.Layout(body));
        }

        await _audit.LogAsync("USERS_IMPORTED", cmd.ActorId, cmd.IpAddress,
            $"CSV import: created {toCreate.Count} user(s)", ct);

        return ServiceResult<ImportResult>.Ok(new ImportResult(toCreate.Count, failures));
    }
}
