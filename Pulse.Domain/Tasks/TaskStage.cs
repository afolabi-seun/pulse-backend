namespace Pulse.Domain.Tasks;

/// <summary>Which half of a two-part Backend→Frontend task is currently in play — see
/// <see cref="PulseTask.RequiresFrontendHandoff"/>. Purely informational: it never gates
/// <see cref="TaskStatus"/> or any of the workflows that switch over it (escalation, board
/// columns, active-workload counting).</summary>
public enum TaskStage { Backend, Frontend }
