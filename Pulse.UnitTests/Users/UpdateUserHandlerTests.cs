using Pulse.Application.Common.Interfaces;
using Pulse.Application.Users.Commands;
using Pulse.Domain.Engineers;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Users;

public class UpdateUserHandlerTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();

    private UpdateUserHandler CreateHandler() =>
        new(_engineers.Object, _teams.Object, _audit.Object, _realtime.Object);

    private Engineer SeedEngineer(string role = Roles.Engineer)
    {
        var engineer = Engineer.Create("Dana Dev", "dana@pulse.io", "hash", role, 20, 14);
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        return engineer;
    }

    [Fact]
    public async Task Role_change_pushes_role_changed_to_the_target_user()
    {
        var engineer = SeedEngineer(Roles.Engineer);
        var actorId = Guid.NewGuid();

        var result = await CreateHandler().Handle(
            new UpdateUserCommand(engineer.Id, Roles.TeamLead, null, null, null, null, null, actorId, null), default);

        result.IsSuccess.Should().BeTrue();
        _realtime.Verify(r => r.SendRoleChangedAsync(engineer.Id, default), Times.Once);
    }

    [Fact]
    public async Task Setting_role_to_the_same_value_does_not_push()
    {
        var engineer = SeedEngineer(Roles.Engineer);
        var actorId = Guid.NewGuid();

        var result = await CreateHandler().Handle(
            new UpdateUserCommand(engineer.Id, Roles.Engineer, null, null, null, null, null, actorId, null), default);

        result.IsSuccess.Should().BeTrue();
        _realtime.Verify(r => r.SendRoleChangedAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task Update_with_no_role_field_does_not_push()
    {
        var engineer = SeedEngineer(Roles.Engineer);
        var actorId = Guid.NewGuid();

        var result = await CreateHandler().Handle(
            new UpdateUserCommand(engineer.Id, null, true, null, null, null, null, actorId, null), default);

        result.IsSuccess.Should().BeTrue();
        _realtime.Verify(r => r.SendRoleChangedAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task Invalid_role_fails_before_saving_and_does_not_push()
    {
        var engineer = SeedEngineer(Roles.Engineer);
        var actorId = Guid.NewGuid();

        var result = await CreateHandler().Handle(
            new UpdateUserCommand(engineer.Id, "not_a_real_role", null, null, null, null, null, actorId, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        _engineers.Verify(r => r.SaveChangesAsync(default), Times.Never);
        _realtime.Verify(r => r.SendRoleChangedAsync(It.IsAny<Guid>(), default), Times.Never);
    }
}
