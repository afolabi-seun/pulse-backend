using Pulse.Domain.Engineers;

namespace Pulse.Application.Auth;

/// <summary>
/// Single source of truth for which roles satisfy each named capability. Pulse used to have
/// eleven separate <c>IAuthorizationFilter</c> attribute classes, each hardcoding its own
/// <c>HashSet&lt;string&gt;</c> of allowed roles — the same logical capability copy-pasted across
/// classes with nothing enforcing agreement between them. That's what let three of them miss
/// <c>head_of_functional</c> when it was added to the domain (see docs/rbac-consolidation.md).
/// Every entry here replaces one of those classes; <c>Pulse.Api.Authorization.RequiresCapabilityAttribute</c>
/// is the one generic filter that reads from this map (it lives here, in Application, rather than
/// in Api, so <see cref="AuthDto.AuthUserDto"/> construction can also resolve capabilities without
/// Application depending on Api). Adding a role to a capability is now a one-line change in
/// exactly one place.
/// </summary>
public static class CapabilityRegistry
{
    public sealed record Capability(IReadOnlySet<string> AllowedRoles, string ForbiddenMessage);

    // Capability keys — pass one of these to [RequiresCapability(...)]. Names describe the
    // former attribute class so the migration is traceable; see docs/rbac-consolidation.md's
    // "open questions" for renaming these to per-action keys in a later phase.
    public const string ProductManagerOrAbove = "product-manager-or-above";
    public const string PmOrAbove             = "pm-or-above";
    public const string TeamLeadOrAbove       = "team-lead-or-above";
    public const string TeamLeadOrHeadOnly    = "team-lead-or-head-only";
    public const string AnyHead               = "any-head";
    public const string HeadOnly              = "head-only";
    public const string HeadOfPmoOnly         = "head-of-pmo-only";
    public const string PmoOnly               = "pmo-only";
    public const string ProjectCreatorOrAbove = "project-creator-or-above";
    public const string ProjectDeleterOrAbove = "project-deleter-or-above";
    public const string TeamCreatorOrAbove    = "team-creator-or-above";
    /// <summary>PMO, Functional, and Product heads (plus PMO's and Product's manager-tier roles) —
    /// who may create or edit a sprint. Deliberately its own capability rather than reusing
    /// ProjectCreatorOrAbove (same role set today), matching this file's convention of one
    /// capability per action so sprint- and project-creation rules stay free to diverge later.</summary>
    public const string SprintCreatorOrAbove  = "sprint-creator-or-above";
    /// <summary>Executive only. Deliberately its own capability rather than folded into an
    /// existing one — every other capability here also gates at least one write endpoint
    /// somewhere, and Executive must never pass any of those. Pair with an existing read
    /// capability via [RequiresCapability(existing, ExecutiveRead)] on read-only GET endpoints;
    /// never add it to a capability that also guards a POST/PUT/PATCH/DELETE.</summary>
    public const string ExecutiveRead         = "executive-read";
    /// <summary>HR only. Deliberately its own capability, not merged with ExecutiveRead — the
    /// two roles are conceptually distinct (people/reporting vs. general stakeholder) and must
    /// be free to diverge. Like ExecutiveRead, never combine with a capability that also gates
    /// a write endpoint. Added to the queries covering Users, Teams, Performance, Time Summary and
    /// the org-wide/reporting queries (Projects, Escalations, Standup Summary, PMO/Leadership
    /// Report, Project Members/Throughput, Org Trend). Read access to the delivery internals
    /// (tasks, sprints, epics, wiki, comments, estimation) is granted separately, to Executive, HR
    /// and Accountant alike, through Roles.IsOrgReadOnlyViewer at each read query.</summary>
    public const string HrRead                = "hr-read";
    /// <summary>Accountant only. Deliberately its own capability, not merged with HrRead or
    /// ExecutiveRead — see the Accountant role's own doc comment. Like the other *Read
    /// capabilities, never combine with one that also gates a write endpoint. Added to Projects,
    /// Reports (PMO and Leadership), Time Summary, Standup Summary and the engineer/team lists those
    /// pages depend on; delivery-internal reads go through Roles.IsOrgReadOnlyViewer instead.</summary>
    public const string AccountantRead        = "accountant-read";
    /// <summary>Team lead and above — everyone from Team Lead through ProjectManager/ProductManager
    /// and every department head, including Head of PMO. Org-wide roles (PM/ProductManager/
    /// HeadOfPmo) already see every project regardless, so this grants no new access for them —
    /// it's a personal "projects I'm tracking" bookmark list, same as it is for a department head
    /// watching a project outside their own department. Deliberately its own capability rather
    /// than reusing TeamLeadOrAbove, since Engineer/Designer still shouldn't get a follow button.</summary>
    public const string ProjectFollow         = "project-follow";
    /// <summary>Every role except PMO (HeadOfPmo + ProjectManager) and Executive — who may submit
    /// time entries. Deliberately its own set: every other capability here either excludes
    /// Engineer/Designer entirely or includes ProjectManager/HeadOfPmo, neither of which fits this
    /// "everyone doing delivery work" grouping. HR and Accountant were added on top of that
    /// grouping by explicit request — not delivery work, but they need to log Admin-category
    /// hours (and hours on tasks in projects PMO has added them to) like anyone else.
    /// Not the same population as <see cref="CheckInExpected"/> any more now that HR and
    /// Accountant are in this one but not that one — don't reuse this set for anything but time-entry eligibility.</summary>
    public const string TimeEntrySubmitter    = "time-entry-submitter";
    /// <summary>Roles expected to submit a daily standup check-in — the original "everyone doing
    /// delivery work" grouping TimeEntrySubmitter used to be exactly equal to, before HR was added
    /// there for time-tracking access without also being expected to do daily standups. Kept as
    /// its own capability so the two can keep diverging safely; used only for filtering (nothing
    /// gates an endpoint with it), same as TimeEntrySubmitter is reused by TimeEntryReminderJob.</summary>
    public const string CheckInExpected       = "check-in-expected";
    /// <summary>Roles that may keep personal tasks — private to-dos in a project of their own, to map
    /// time to. HR and Accountant: they log time but belong to no team and have no project of their own
    /// to put tasks in. Everyone else already works inside real projects.</summary>
    public const string PersonalTaskCreator   = "personal-task-creator";

    public static readonly IReadOnlyDictionary<string, Capability> All = new Dictionary<string, Capability>
    {
        // Was ProductManagerOrAboveAttribute.
        [ProductManagerOrAbove] = new(
            new HashSet<string>
            {
                Roles.ProductManager, Roles.ProjectManager,
                Roles.HeadOfRnD, Roles.HeadOfProduct, Roles.HeadOfDesign, Roles.HeadOfPmo, Roles.HeadOfFunctional, Roles.HeadOfCoreBanking, Roles.HeadOfInfraDevOps,
            },
            "Product manager or above required."),

        // Was PmOrAboveAttribute. Deliberately excludes product_manager/team_lead — see PmOrAbove
        // usages; this is NOT a strict superset of ProductManagerOrAbove.
        [PmOrAbove] = new(
            new HashSet<string>
            {
                Roles.ProjectManager, Roles.ProductManager,
                Roles.HeadOfRnD, Roles.HeadOfProduct, Roles.HeadOfDesign, Roles.HeadOfPmo, Roles.HeadOfFunctional, Roles.HeadOfCoreBanking, Roles.HeadOfInfraDevOps,
            },
            "Project manager or above required."),

        // Was TeamLeadOrAboveAttribute.
        [TeamLeadOrAbove] = new(
            new HashSet<string>
            {
                Roles.TeamLead, Roles.ProductManager, Roles.ProjectManager,
                Roles.HeadOfRnD, Roles.HeadOfProduct, Roles.HeadOfDesign, Roles.HeadOfPmo, Roles.HeadOfFunctional, Roles.HeadOfCoreBanking, Roles.HeadOfInfraDevOps,
            },
            "Team lead or above required."),

        // Was TeamLeadOrHeadOnlyAttribute. Excludes project_manager/product_manager.
        [TeamLeadOrHeadOnly] = new(
            new HashSet<string>
            {
                Roles.TeamLead,
                Roles.HeadOfRnD, Roles.HeadOfProduct, Roles.HeadOfDesign, Roles.HeadOfPmo, Roles.HeadOfFunctional, Roles.HeadOfCoreBanking, Roles.HeadOfInfraDevOps,
            },
            "Team lead or department head required."),

        // Project following — team lead and above. See ProjectFollow's own doc comment above.
        [ProjectFollow] = new(
            new HashSet<string>
            {
                Roles.TeamLead, Roles.ProjectManager, Roles.ProductManager,
                Roles.HeadOfRnD, Roles.HeadOfProduct, Roles.HeadOfDesign, Roles.HeadOfPmo, Roles.HeadOfFunctional, Roles.HeadOfCoreBanking, Roles.HeadOfInfraDevOps,
            },
            "Team lead or above required."),

        // Was AnyHeadAttribute — every department-head role, nothing else.
        [AnyHead] = new(Roles.HeadRoles, "Department head access required."),

        // Was HeadOnlyAttribute — head_of_rd exclusively (audit log, Hangfire dashboard).
        [HeadOnly] = new(new HashSet<string> { Roles.HeadOfRnD }, "Head of R&D access required."),

        // Was HeadOfPmoOnlyAttribute — head_of_pmo exclusively.
        [HeadOfPmoOnly] = new(new HashSet<string> { Roles.HeadOfPmo }, "Head of PMO access required."),

        // Was PmoOnlyAttribute. Gates team management; deliberately narrower than PmOrAbove.
        [PmoOnly] = new(
            new HashSet<string> { Roles.HeadOfPmo, Roles.ProjectManager },
            "PMO access required."),

        // Was ProjectCreatorOrAboveAttribute — PMO plus the heads whose department can own a
        // project directly, plus ProductManager ("Product Lead") per PM request. Deliberately its
        // own set rather than widening PmoOnly. HeadOfFunctional already covers "Functional Lead" —
        // there's no separate manager-tier role for Functional the way ProductManager pairs with
        // HeadOfProduct.
        [ProjectCreatorOrAbove] = new(
            new HashSet<string> { Roles.HeadOfPmo, Roles.ProjectManager, Roles.HeadOfProduct, Roles.HeadOfFunctional, Roles.ProductManager },
            "Project creation access required."),

        // Was ProjectDeleterOrAboveAttribute — deliberately narrower than ProjectCreatorOrAbove
        // (no HeadOfFunctional). Do not merge into a broader capability.
        [ProjectDeleterOrAbove] = new(
            new HashSet<string> { Roles.HeadOfPmo, Roles.ProjectManager, Roles.HeadOfProduct },
            "Project deletion access required."),

        // Was TeamCreatorOrAboveAttribute — PMO plus Product's manager and head.
        [TeamCreatorOrAbove] = new(
            new HashSet<string> { Roles.HeadOfPmo, Roles.ProjectManager, Roles.HeadOfProduct, Roles.ProductManager },
            "Team creation access required."),

        // Sprint creation/edit — PMO, Functional, and Product heads only. See SprintCreatorOrAbove's
        // own doc comment above.
        [SprintCreatorOrAbove] = new(
            new HashSet<string> { Roles.HeadOfPmo, Roles.ProjectManager, Roles.HeadOfProduct, Roles.HeadOfFunctional, Roles.ProductManager },
            "Sprint creation access required."),

        // Executive — read-only, org-wide. See the ExecutiveRead doc comment above: this must
        // never be added alongside a capability that also gates a write endpoint.
        [ExecutiveRead] = new(
            new HashSet<string> { Roles.Executive },
            "Executive access required."),

        // HR — read-only, org-wide, its own capability. See the HrRead doc comment above.
        [HrRead] = new(
            new HashSet<string> { Roles.HR },
            "HR access required."),

        // Accountant — read-only, org-wide, its own capability. See the AccountantRead doc comment above.
        [AccountantRead] = new(
            new HashSet<string> { Roles.Accountant },
            "Accountant access required."),

        // Who may submit time entries — every role except PMO and Executive, plus HR. See the
        // TimeEntrySubmitter doc comment above.
        [TimeEntrySubmitter] = new(
            new HashSet<string>
            {
                Roles.Engineer, Roles.TeamLead, Roles.Designer, Roles.ProductManager,
                Roles.HeadOfRnD, Roles.HeadOfProduct, Roles.HeadOfDesign, Roles.HeadOfFunctional, Roles.HeadOfCoreBanking, Roles.HeadOfInfraDevOps,
                Roles.HR, Roles.Accountant,
            },
            "Time tracking access required."),

        // Who's expected to do a daily standup check-in — see the CheckInExpected doc comment
        // above for why this isn't just TimeEntrySubmitter any more.
        [CheckInExpected] = new(
            new HashSet<string>
            {
                Roles.Engineer, Roles.TeamLead, Roles.Designer, Roles.ProductManager,
                Roles.HeadOfRnD, Roles.HeadOfProduct, Roles.HeadOfDesign, Roles.HeadOfFunctional, Roles.HeadOfCoreBanking, Roles.HeadOfInfraDevOps,
            },
            "Check-in access required."),

        [PersonalTaskCreator] = new(
            new HashSet<string> { Roles.HR, Roles.Accountant },
            "Personal tasks are available to HR and Accountant."),
    };

    /// <summary>
    /// Every capability key a given role satisfies — what a client actually needs to decide what
    /// to render, without re-deriving role-to-capability logic locally. Embedded in
    /// <see cref="AuthDto.AuthUserDto"/> at login/refresh.
    /// </summary>
    public static IReadOnlyList<string> ResolveFor(string role) =>
        All.Where(kv => kv.Value.AllowedRoles.Contains(role)).Select(kv => kv.Key).ToList();
}
