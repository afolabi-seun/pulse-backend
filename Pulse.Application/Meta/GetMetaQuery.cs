using Pulse.Application.Common;
using Pulse.Application.Overwork;
using Pulse.Application.Thresholds;
using Pulse.Domain.Engineers;
using Pulse.Domain.Sprints;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;
using DomainTaskType   = Pulse.Domain.Tasks.TaskType;
using MediatR;

namespace Pulse.Application.Meta;

public record EnumMetaDto(string Value, string Label);

public record RoleMetaDto(
    string Value,
    string Label,
    int Rank,
    bool IsDeptHead,
    string Group,
    IReadOnlyList<string>? AssignableRoles,
    bool CanCreateProjects,
    bool CanDeleteProjects);

public record AppMetaDto(
    IReadOnlyList<RoleMetaDto> Roles,
    IReadOnlyList<EnumMetaDto> TaskStatuses,
    IReadOnlyList<EnumMetaDto> TaskTypes,
    IReadOnlyList<EnumMetaDto> SprintStatuses,
    IReadOnlyList<PointScaleEntryDto> PointScale,
    IReadOnlyList<PriorityScaleEntryDto> PriorityScale);

public record GetMetaQuery : IRequest<ServiceResult<AppMetaDto>>;

public class GetMetaHandler : IRequestHandler<GetMetaQuery, ServiceResult<AppMetaDto>>
{
    private readonly OverworkThresholds _thresholds;

    public GetMetaHandler(OverworkThresholds thresholds) => _thresholds = thresholds;

    private static readonly IReadOnlyList<RoleMetaDto> RoleMeta =
    [
        new(Roles.Engineer,       "Engineer",        0, false, "Engineering", null, false, false),
        new(Roles.TeamLead,       "Team Lead",       1, false, "Engineering", null, false, false),
        new(Roles.HeadOfRnD,     "Head of R&D",     4, true,  "Engineering", Roles.CreatableByDeptHead(Roles.HeadOfRnD).ToList(), false, false),
        new(Roles.Designer,       "Designer",        0, false, "Design",      null, false, false),
        new(Roles.HeadOfDesign,  "Head of Design",   4, true,  "Design",      Roles.CreatableByDeptHead(Roles.HeadOfDesign).ToList(), false, false),
        new(Roles.ProductManager, "Product Manager", 2, false, "Product",     null, true, false),
        new(Roles.HeadOfProduct, "Head of Product",  4, true,  "Product",     Roles.CreatableByDeptHead(Roles.HeadOfProduct).ToList(), true, true),
        new(Roles.ProjectManager, "Project Manager", 3, false, "PMO",         null, true, true),
        new(Roles.HeadOfPmo,     "Head of PMO",      4, true,  "PMO",         null, true, true),
        new(Roles.HeadOfFunctional, "Head of Functional", 4, true, "Functional", Roles.CreatableByDeptHead(Roles.HeadOfFunctional).ToList(), true, false),
        new(Roles.HeadOfCoreBanking, "Head of Core Banking", 4, true, "Core Banking", Roles.CreatableByDeptHead(Roles.HeadOfCoreBanking).ToList(), false, false),
        new(Roles.HeadOfInfraDevOps, "Head of Infra/DevOps", 4, true, "Infra/DevOps", Roles.CreatableByDeptHead(Roles.HeadOfInfraDevOps).ToList(), false, false),
        new(Roles.Executive,      "Executive",       5, false, "Executive",   null, false, false),
        new(Roles.HR,             "HR",              6, false, "HR",          null, false, false),
        new(Roles.Accountant,     "Accountant",       7, false, "Accounting",  null, false, false),
    ];

    private static readonly IReadOnlyList<EnumMetaDto> TaskStatusMeta =
    [
        new(Camel(nameof(DomainTaskStatus.Backlog)), "Backlog"),
        new(Camel(nameof(DomainTaskStatus.Active)),  "Active"),
        new(Camel(nameof(DomainTaskStatus.Blocked)), "Blocked"),
        new(Camel(nameof(DomainTaskStatus.InQa)),    "In QA"),
        new(Camel(nameof(DomainTaskStatus.Paused)),  "Paused"),
        new(Camel(nameof(DomainTaskStatus.Done)),    "Done"),
    ];

    private static readonly IReadOnlyList<EnumMetaDto> TaskTypeMeta =
    [
        new(Camel(nameof(DomainTaskType.Feature)), "Feature"),
        new(Camel(nameof(DomainTaskType.Bug)),     "Bug"),
        new(Camel(nameof(DomainTaskType.Test)),    "Test"),
        new(Camel(nameof(DomainTaskType.Review)),  "Review"),
        new(Camel(nameof(DomainTaskType.Chore)),   "Chore"),
    ];

    private static readonly IReadOnlyList<EnumMetaDto> SprintStatusMeta =
    [
        new(nameof(SprintStatus.Planning),  "Planning"),
        new(nameof(SprintStatus.Active),    "Active"),
        new(nameof(SprintStatus.Completed), "Completed"),
    ];

    public Task<ServiceResult<AppMetaDto>> Handle(GetMetaQuery request, CancellationToken ct)
    {
        var pointScale = _thresholds.PointScale
            .Select(e => new PointScaleEntryDto(e.Value, e.Label, e.TimeGuide))
            .ToList();
        var priorityScale = _thresholds.PriorityScale
            .Select(e => new PriorityScaleEntryDto(e.Value, e.Label, e.Criteria))
            .ToList();
        return Task.FromResult(ServiceResult<AppMetaDto>.Ok(
            new AppMetaDto(RoleMeta, TaskStatusMeta, TaskTypeMeta, SprintStatusMeta, pointScale, priorityScale)));
    }

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];
}
