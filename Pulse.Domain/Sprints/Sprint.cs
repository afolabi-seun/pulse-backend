using Pulse.Domain.Common;

namespace Pulse.Domain.Sprints;

public class Sprint : Entity
{
    public Guid TeamId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Goal { get; private set; }
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public SprintStatus Status { get; private set; } = SprintStatus.Planning;
    public int? CapacityPoints { get; private set; }
    public DateOnly? ShowAndTellDate { get; private set; }
    public string? ShowAndTellNotes { get; private set; }

    private Sprint() { }

    public static Sprint Create(Guid teamId, Guid? projectId, string name, DateOnly startDate, DateOnly endDate, string? goal = null)
    {
        if (endDate <= startDate)
            throw new DomainException("End date must be after start date.");

        return new()
        {
            TeamId = teamId,
            ProjectId = projectId,
            Name = name,
            Goal = goal,
            StartDate = startDate,
            EndDate = endDate
        };
    }

    public void UpdateDetails(string name, string? goal, DateOnly startDate, DateOnly endDate)
    {
        if (Status == SprintStatus.Completed)
            throw new DomainException("Cannot edit a completed sprint.");
        if (endDate <= startDate)
            throw new DomainException("End date must be after start date.");

        Name = name;
        Goal = goal;
        StartDate = startDate;
        EndDate = endDate;
    }

    /// <summary>Transitions Planning → Active. Throws if already Active or Completed.</summary>
    public void Activate()
    {
        if (Status != SprintStatus.Planning)
            throw new DomainException($"Sprint is already {Status}.");
        Status = SprintStatus.Active;
    }

    public void SetCapacity(int? capacityPoints) => CapacityPoints = capacityPoints;

    public void SetCeremony(DateOnly? showAndTellDate, string? notes)
    {
        ShowAndTellDate = showAndTellDate;
        ShowAndTellNotes = notes;
    }

    /// <summary>Transitions Active → Completed. Throws if not Active.</summary>
    public void Complete()
    {
        if (Status != SprintStatus.Active)
            throw new DomainException("Only an active sprint can be completed.");
        Status = SprintStatus.Completed;
    }
}
