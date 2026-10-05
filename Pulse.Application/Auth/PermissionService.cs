namespace Pulse.Application.Auth;

/// <summary>
/// Maps engineer roles to their allowed permission codes.
/// Permissions are role-derived in V1 — no per-user overrides.
/// </summary>
public static class PermissionService
{
    // Role hierarchy rank: higher index ⊇ lower index
    private static readonly string[] EngineerPermissions =
    [
        "dashboard:view",
        "tasks:view",
        "projects:view",
        "checkins:submit",
        "feedback:submit",
        "notifications:view",
        "vitals:submit",
    ];

    private static readonly string[] TeamLeadPermissions =
    [
        .. EngineerPermissions,
        "checkins:view",
        "escalations:view",
        "engineers:view",
        "overwork:view",
    ];

    private static readonly string[] ProjectManagerPermissions =
    [
        .. TeamLeadPermissions,
        "tasks:manage",
        "projects:manage",
        "overwork:override",
        "reports:view",
        "vitals:view",
    ];

    private static readonly string[] AnyHeadPermissions =
    [
        .. ProjectManagerPermissions,
        "feedback:view",
        "users:manage",
        "audit:view",
    ];

    private static readonly string[] HeadOfRnDPermissions =
    [
        .. AnyHeadPermissions,
        "thresholds:manage",
    ];

    public static IReadOnlyList<string> For(string role) => role switch
    {
        "team_lead"        => TeamLeadPermissions,
        "project_manager"  => ProjectManagerPermissions,
        "head_of_rd"       => HeadOfRnDPermissions,
        "head_of_product"  => AnyHeadPermissions,
        "head_of_design"   => AnyHeadPermissions,
        "head_of_pmo"      => AnyHeadPermissions,
        "head_of_functional" => AnyHeadPermissions,
        "head_of_core_banking" => AnyHeadPermissions,
        "head_of_infra_devops" => AnyHeadPermissions,
        _                  => EngineerPermissions, // engineer + unknown roles get baseline
    };
}
