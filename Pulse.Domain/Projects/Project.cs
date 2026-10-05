using System.Text.RegularExpressions;
using Pulse.Domain.Common;

namespace Pulse.Domain.Projects;

public class Project : Entity
{
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public ProjectStatus Status { get; private set; } = ProjectStatus.Active;
    public Guid? OwnerTeamId { get; private set; }
    /// <summary>Owning organization. Phase 0 of multi-tenancy: always the default org (set by the
    /// column default when left null on insert), and not yet read anywhere.</summary>
    public Guid? OrganizationId { get; private set; }
    /// <summary>Short, unique, uppercase key used as the prefix for every task's display ID in this
    /// project (e.g. "NOTIF" for task keys like NOTIF-011). Uniqueness is enforced by callers
    /// (ProjectCodeGenerator + a DB unique index) — this class only enforces the format.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Set only on a person's private "personal tasks" project (see <see cref="CreatePersonal"/>) —
    /// the engineer it belongs to. Null for every ordinary project. A personal project is kept out of
    /// every org-level project list, picker and report; only its owner works in it.</summary>
    public Guid? PersonalOwnerId { get; private set; }

    private static readonly Regex CodePattern = new("^[A-Z][A-Z0-9]{1,9}$", RegexOptions.Compiled);

    private Project() { }

    /// <summary>An empty/whitespace <paramref name="code"/> auto-derives a base code from
    /// <paramref name="name"/> (uppercase letters/digits, first 6) — a bare fallback with no
    /// uniqueness check, since this Domain class can't query existing projects. Real callers that
    /// care about a clean, deduped code should resolve one via
    /// <c>Pulse.Application.Projects.ProjectCodeGenerator</c> before calling in; this fallback
    /// exists so callers that genuinely don't care (most tests) don't all need to.</summary>
    public static Project Create(string name, string? description = null, Guid? ownerTeamId = null, string? code = null)
    {
        var resolvedCode = string.IsNullOrWhiteSpace(code) ? DeriveFallbackCode(name) : code;
        ValidateCode(resolvedCode);
        return new() { Name = name, Code = resolvedCode, Description = description, OwnerTeamId = ownerTeamId };
    }

    /// <summary>The private project that holds one person's personal tasks (to-dos they can log time
    /// against without belonging to any real project). One per engineer, created on first use.</summary>
    public static Project CreatePersonal(Guid ownerId, string ownerName, string code)
    {
        ValidateCode(code);
        return new()
        {
            Name = $"{ownerName} — Personal",
            Description = "Personal tasks",
            Code = code,
            PersonalOwnerId = ownerId,
        };
    }

    private static string DeriveFallbackCode(string name)
    {
        var letters = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (letters.Length == 0) return "PROJ";
        return letters.Length <= 6 ? letters : letters[..6];
    }

    public void SetCode(string code)
    {
        ValidateCode(code);
        Code = code;
    }

    private static void ValidateCode(string code)
    {
        if (!CodePattern.IsMatch(code))
            throw new DomainException("Project code must be 2-10 uppercase letters/digits, starting with a letter.");
    }

    public void SetOwnerTeam(Guid? teamId) => OwnerTeamId = teamId;

    public void Update(string name, string? description)
    {
        Name = name;
        Description = description;
    }

    public void Archive() => Status = ProjectStatus.Archived;

    /// <summary>Pauses the project. Callers are responsible for pausing its tasks (see PauseProjectCommand).</summary>
    public void Pause()
    {
        if (Status != ProjectStatus.Active)
            throw new DomainException("Only an active project can be paused.");
        Status = ProjectStatus.Paused;
    }

    /// <summary>Resumes the project. Callers are responsible for resuming its tasks (see ResumeProjectCommand).</summary>
    public void Resume()
    {
        if (Status != ProjectStatus.Paused)
            throw new DomainException("Project is not paused.");
        Status = ProjectStatus.Active;
    }
}
