using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Users.Queries;

public record EmailExistsDto(string Email, bool Exists);

public record CheckEmailsExistQuery(IReadOnlyList<string> Emails) : IRequest<ServiceResult<IReadOnlyList<EmailExistsDto>>>;

/// <summary>
/// Checks a batch of candidate emails against existing accounts — e.g. before inviting a list of
/// people, to see who's already a user without needing direct database access.
/// </summary>
public class CheckEmailsExistHandler : IRequestHandler<CheckEmailsExistQuery, ServiceResult<IReadOnlyList<EmailExistsDto>>>
{
    private const int MaxEmails = 200;

    private readonly IEngineerRepository _engineers;

    public CheckEmailsExistHandler(IEngineerRepository engineers) => _engineers = engineers;

    public async Task<ServiceResult<IReadOnlyList<EmailExistsDto>>> Handle(CheckEmailsExistQuery query, CancellationToken ct)
    {
        var emails = query.Emails
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct()
            .ToList();

        if (emails.Count == 0)
            return ServiceResult<IReadOnlyList<EmailExistsDto>>.Fail("BUSINESS_RULE_VIOLATION", "At least one email is required.");

        if (emails.Count > MaxEmails)
            return ServiceResult<IReadOnlyList<EmailExistsDto>>.Fail("BUSINESS_RULE_VIOLATION", $"Cannot check more than {MaxEmails} emails at once.");

        var existing = await _engineers.GetExistingEmailsAsync(emails, ct);
        var existingSet = existing.ToHashSet();

        var result = emails.Select(e => new EmailExistsDto(e, existingSet.Contains(e))).ToList();
        return ServiceResult<IReadOnlyList<EmailExistsDto>>.Ok(result);
    }
}
