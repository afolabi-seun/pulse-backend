using Pulse.Application.Common.Interfaces;
using Pulse.Application.Users.Queries;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Users;

public class CheckEmailsExistHandlerTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();

    private CheckEmailsExistHandler CreateHandler() => new(_engineers.Object);

    [Fact]
    public async Task Returns_exists_true_only_for_emails_already_in_the_database()
    {
        _engineers
            .Setup(r => r.GetExistingEmailsAsync(It.IsAny<IReadOnlyList<string>>(), default))
            .ReturnsAsync(new[] { "known@pulse.io" });

        var result = await CreateHandler().Handle(
            new CheckEmailsExistQuery(new[] { "known@pulse.io", "unknown@pulse.io" }), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEquivalentTo(new[]
        {
            new EmailExistsDto("known@pulse.io", true),
            new EmailExistsDto("unknown@pulse.io", false),
        });
    }

    [Fact]
    public async Task Returns_BUSINESS_RULE_VIOLATION_when_no_emails_given()
    {
        var result = await CreateHandler().Handle(new CheckEmailsExistQuery(Array.Empty<string>()), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Ignores_blank_entries_and_deduplicates()
    {
        _engineers
            .Setup(r => r.GetExistingEmailsAsync(It.IsAny<IReadOnlyList<string>>(), default))
            .ReturnsAsync(Array.Empty<string>());

        var result = await CreateHandler().Handle(
            new CheckEmailsExistQuery(new[] { "a@pulse.io", "  ", "a@pulse.io", "" }), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().ContainSingle().Which.Email.Should().Be("a@pulse.io");
    }

    [Fact]
    public async Task Returns_BUSINESS_RULE_VIOLATION_when_over_the_batch_limit()
    {
        var tooMany = Enumerable.Range(0, 201).Select(i => $"user{i}@pulse.io").ToArray();

        var result = await CreateHandler().Handle(new CheckEmailsExistQuery(tooMany), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }
}
