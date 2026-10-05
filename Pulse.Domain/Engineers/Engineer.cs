using Pulse.Domain.Common;
using Pulse.Domain.Tasks;

namespace Pulse.Domain.Engineers;

public class Engineer : Entity
{
    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string Role { get; private set; } = Roles.Engineer;
    public int BaselinePoints { get; private set; }
    public int BaselineCycleDays { get; private set; }
    public string? Team { get; private set; }
    public Guid? TeamId { get; private set; }
    public bool IsQa { get; private set; }
    public Discipline? Discipline { get; private set; }
    public bool IsActive { get; private set; } = true;
    public int FailedLoginAttempts { get; private set; }
    public DateTime? LockedUntil { get; private set; }
    public string? PasswordResetToken { get; private set; }
    public DateTime? PasswordResetTokenExpiresAt { get; private set; }

    private Engineer() { }

    public static Engineer Create(string name, string email, string passwordHash, string role,
        int baselinePoints, int baselineCycleDays)
    {
        ValidateBaseline(baselinePoints, baselineCycleDays);
        return new()
        {
            Name = name,
            Email = email,
            PasswordHash = passwordHash,
            Role = role,
            BaselinePoints = baselinePoints,
            BaselineCycleDays = baselineCycleDays
        };
    }

    public void UpdateBaseline(int points, int cycleDays)
    {
        ValidateBaseline(points, cycleDays);
        BaselinePoints = points;
        BaselineCycleDays = cycleDays;
    }

    // OverworkSignalsCalculator divides by BaselinePoints and multiplies thresholds by
    // BaselineCycleDays — a zero or negative value there doesn't just skew the math, it makes
    // the load signal trip on any single active task. The API already validates this on the one
    // human-facing edit form (UpdateBaselineRequestValidator); guarding it here too means no
    // future caller (an import path, a bootstrap default, a seed script) can reintroduce it.
    private static void ValidateBaseline(int points, int cycleDays)
    {
        if (points <= 0)
            throw new DomainException("Baseline points must be greater than zero.");
        if (cycleDays <= 0)
            throw new DomainException("Baseline cycle days must be greater than zero.");
    }

    public void RecordFailedLogin()
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= 5)
            LockedUntil = DateTime.UtcNow.AddMinutes(15);
    }

    public void ResetLoginAttempts()
    {
        FailedLoginAttempts = 0;
        LockedUntil = null;
    }

    public bool IsLockedOut() => LockedUntil.HasValue && LockedUntil > DateTime.UtcNow;

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    /// <summary>Updates the engineer's role. Throws <see cref="DomainException"/> for unrecognised role strings.</summary>
    public void UpdateRole(string role)
    {
        if (!Roles.All.Contains(role))
            throw new DomainException($"'{role}' is not a valid role.");
        Role = role;
    }

    public void SetTeam(string? team) => Team = string.IsNullOrWhiteSpace(team) ? null : team.Trim();

    public void AssignToTeam(Guid? teamId) => TeamId = teamId;

    public void SetIsQa(bool isQa) => IsQa = isQa;

    public void SetDiscipline(Discipline? discipline) => Discipline = discipline;

    public void UnlockAccount()
    {
        FailedLoginAttempts = 0;
        LockedUntil = null;
    }

    public void SetPasswordResetToken(string token, DateTime expiresAt)
    {
        PasswordResetToken = token;
        PasswordResetTokenExpiresAt = expiresAt;
    }

    public void SetPasswordHash(string hash)
    {
        PasswordHash = hash;
        PasswordResetToken = null;
        PasswordResetTokenExpiresAt = null;
    }
}

public static class Roles
{
    public const string Engineer       = "engineer";
    public const string TeamLead       = "team_lead";
    public const string Designer       = "designer";
    public const string ProductManager = "product_manager";
    public const string ProjectManager = "project_manager";
    public const string HeadOfRnD     = "head_of_rd";
    public const string HeadOfProduct  = "head_of_product";
    public const string HeadOfDesign   = "head_of_design";
    public const string HeadOfPmo      = "head_of_pmo";
    public const string HeadOfFunctional = "head_of_functional";
    public const string HeadOfCoreBanking = "head_of_core_banking";
    public const string HeadOfInfraDevOps = "head_of_infra_devops";
    /// <summary>External, read-only stakeholder (business owner, partner). Org-wide visibility,
    /// no team, no write access anywhere — see docs on CapabilityRegistry.ExecutiveRead.</summary>
    public const string Executive = "executive";
    /// <summary>Read-only, org-wide people/reporting stakeholder. No team, no write access
    /// anywhere — its own capability (CapabilityRegistry.HrRead), deliberately not merged with
    /// ExecutiveRead so the two can diverge. Shares Executive's org-wide read access to Projects,
    /// Sprints, Wiki and Standup via <see cref="IsOrgReadOnlyViewer"/>; can additionally log time
    /// and create tasks in projects PMO has added it to as a member.</summary>
    public const string HR = "hr";
    /// <summary>Read-only, org-wide invoicing stakeholder (e.g. an external/contracted
    /// accountant). No team, no write access anywhere — its own capability
    /// (CapabilityRegistry.AccountantRead). Shares the same org-wide read access as Executive
    /// and HR via <see cref="IsOrgReadOnlyViewer"/> (Projects, Sprints, Wiki, Standup, Reports);
    /// can additionally log time and create tasks in projects PMO has added it to as a member.</summary>
    public const string Accountant = "accountant";

    /// <summary>The org-wide, read-only, no-team roles. They bypass the write-coupled
    /// ProjectAccessPolicy on READ queries only (Projects, Sprints, Wiki, epics, task detail) —
    /// deliberately not added to ProjectAccessPolicy.GlobalRoles, which ~15 write commands also
    /// read, so adding them there would silently grant write access everywhere. Use this instead
    /// of hard-coding a role list at each read site.</summary>
    public static bool IsOrgReadOnlyViewer(string? role) => role is Executive or HR or Accountant;

    /// <summary>All valid role values — used for whitelist validation.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Engineer, TeamLead, Designer, ProductManager, ProjectManager,
        HeadOfRnD, HeadOfProduct, HeadOfDesign, HeadOfPmo, HeadOfFunctional, HeadOfCoreBanking, HeadOfInfraDevOps, Executive, HR, Accountant,
    };

    /// <summary>Roles that carry department-head level access.</summary>
    public static readonly IReadOnlySet<string> HeadRoles = new HashSet<string>
    {
        HeadOfRnD, HeadOfProduct, HeadOfDesign, HeadOfPmo, HeadOfFunctional, HeadOfCoreBanking, HeadOfInfraDevOps,
    };

    /// <summary>Roles with unrestricted, org-wide reach in user management — see every user, and
    /// create/update/deactivate a user of any role, not just ones within their own department.
    /// HeadOfProduct is included here to match the org-wide reach it already has everywhere else
    /// in the app (time tracking, check-ins, escalations, teams, reports, engineer lists — see
    /// project_pulse_access_model_decks); user management was the one place it had been left
    /// out. Kept as its own named set (distinct from HeadRoles) since callers need to check it in
    /// several places — ListUsersQuery, UsersController (create/update/delete), and
    /// ImportUsersCommand — and a single source avoids them drifting apart.</summary>
    public static readonly IReadOnlySet<string> UserManagementGlobalRoles = new HashSet<string>
    {
        HeadOfPmo, ProjectManager, HeadOfProduct,
    };

    /// <summary>Roles a non-global caller (department head, or a manager-tier role admitted to
    /// PmOrAbove) is permitted to create. Excludes their own role and any head-level role.
    /// Never reached for a UserManagementGlobalRoles caller — they bypass this restriction
    /// entirely at the call site.</summary>
    public static IReadOnlyList<string> CreatableByDeptHead(string callerRole) => callerRole switch
    {
        HeadOfRnD         => [Engineer, TeamLead],
        HeadOfDesign      => [Designer],
        HeadOfFunctional  => [Engineer, TeamLead],
        HeadOfCoreBanking => [Engineer, TeamLead],
        HeadOfInfraDevOps => [Engineer, TeamLead],
        ProductManager    => [Engineer, TeamLead],
        _                 => [],
    };
}
