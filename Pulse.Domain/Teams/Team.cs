using Pulse.Domain.Common;

namespace Pulse.Domain.Teams;

public class Team : Entity
{
    public string Name { get; private set; } = string.Empty;
    public Guid? TeamLeadId { get; private set; }
    public bool IsActive { get; private set; } = true;
    /// <summary>Vertical this team belongs to — e.g. "Engineering", "Product", "Design", "PMO".</summary>
    public string? Department { get; private set; }

    private Team() { }

    public static Team Create(string name, Guid? teamLeadId = null, string? department = null) =>
        new() { Name = name, TeamLeadId = teamLeadId, Department = department };

    public void Update(string name) => Name = name;

    public void SetDepartment(string? department) =>
        Department = string.IsNullOrWhiteSpace(department) ? null : department.Trim();

    public void SetTeamLead(Guid? teamLeadId) => TeamLeadId = teamLeadId;

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}
