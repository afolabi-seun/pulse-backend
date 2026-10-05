using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects;
using Pulse.Domain.Projects;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record ImportProjectRow(string Name, string? Description, string? OwnerTeam = null);

public record ImportProjectsCommand(
    IReadOnlyList<ImportProjectRow> Rows,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<ImportResult>>;

public class ImportProjectsHandler : IRequestHandler<ImportProjectsCommand, ServiceResult<ImportResult>>
{
    private readonly IProjectRepository  _projects;
    private readonly ITeamRepository     _teams;
    private readonly IAuditLogRepository _audit;

    public ImportProjectsHandler(IProjectRepository projects, ITeamRepository teams, IAuditLogRepository audit)
    {
        _projects = projects;
        _teams    = teams;
        _audit    = audit;
    }

    public async Task<ServiceResult<ImportResult>> Handle(ImportProjectsCommand cmd, CancellationToken ct)
    {
        if (cmd.Rows.Count == 0)
            return ServiceResult<ImportResult>.Fail("BUSINESS_RULE_VIOLATION", "No rows to import.");

        if (cmd.Rows.Count > 500)
            return ServiceResult<ImportResult>.Fail("BUSINESS_RULE_VIOLATION", "Cannot import more than 500 projects at once.");

        var allTeams = await _teams.ListAllAsync(ct);
        var teamByName = allTeams.ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);

        var existingNames = (await _projects.ListActiveAsync(ct))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedCodes = new HashSet<string>(await _projects.GetAllCodesAsync(ct));

        var failures = new List<ImportRowFailure>();
        var created = 0;

        for (var i = 0; i < cmd.Rows.Count; i++)
        {
            var row = cmd.Rows[i];
            var rowNum = i + 2; // 1-based row number, +1 for header

            if (string.IsNullOrWhiteSpace(row.Name))
            {
                failures.Add(new ImportRowFailure(rowNum, "Name is required."));
                continue;
            }

            if (existingNames.Contains(row.Name.Trim()))
            {
                failures.Add(new ImportRowFailure(rowNum, $"Project '{row.Name.Trim()}' already exists — skipped."));
                continue;
            }

            Guid? ownerTeamId = null;
            if (!string.IsNullOrWhiteSpace(row.OwnerTeam) && teamByName.TryGetValue(row.OwnerTeam.Trim(), out var teamId))
                ownerTeamId = teamId;

            var code = ProjectCodeGenerator.MakeUnique(ProjectCodeGenerator.DeriveBase(row.Name.Trim()), usedCodes);
            var project = Project.Create(
                row.Name.Trim(),
                DescriptionSanitizer.SanitizePlainText(string.IsNullOrWhiteSpace(row.Description) ? null : row.Description.Trim()),
                ownerTeamId,
                code);

            await _projects.AddAsync(project, ct);
            existingNames.Add(row.Name.Trim());
            created++;
        }

        if (created > 0)
        {
            await _projects.SaveChangesAsync(ct);
            await _audit.LogAsync("PROJECT_IMPORTED", cmd.ActorId, cmd.IpAddress,
                $"CSV import: created {created} project(s)", ct);
        }

        return ServiceResult<ImportResult>.Ok(new ImportResult(created, failures));
    }
}
