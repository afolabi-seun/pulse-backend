namespace Pulse.Application.Common;

public record ImportResult(int Created, IReadOnlyList<ImportRowFailure> Failures);

public record ImportRowFailure(int Row, string Error);
