using Pulse.Domain.Common;
using Pulse.Domain.Engineers;
using FluentAssertions;

namespace Pulse.UnitTests.Engineers;

public class EngineerTests
{
    private static Engineer NewEngineer(string role = Roles.Engineer) =>
        Engineer.Create("Dev User", "dev@pulse.io", "hashed_pw", role, 20, 14);

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_sets_all_fields()
    {
        var eng = Engineer.Create("Alice", "alice@pulse.io", "pw_hash", Roles.ProjectManager, 30, 21);

        eng.Name.Should().Be("Alice");
        eng.Email.Should().Be("alice@pulse.io");
        eng.PasswordHash.Should().Be("pw_hash");
        eng.Role.Should().Be(Roles.ProjectManager);
        eng.BaselinePoints.Should().Be(30);
        eng.BaselineCycleDays.Should().Be(21);
        eng.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_defaults_to_no_lockout()
    {
        var eng = NewEngineer();

        eng.FailedLoginAttempts.Should().Be(0);
        eng.LockedUntil.Should().BeNull();
        eng.IsLockedOut().Should().BeFalse();
    }

    // ── Login lockout ─────────────────────────────────────────────────────────

    [Fact]
    public void RecordFailedLogin_increments_counter()
    {
        var eng = NewEngineer();

        eng.RecordFailedLogin();
        eng.RecordFailedLogin();

        eng.FailedLoginAttempts.Should().Be(2);
    }

    [Fact]
    public void RecordFailedLogin_locks_account_after_5_failures()
    {
        var eng = NewEngineer();

        for (var i = 0; i < 5; i++)
            eng.RecordFailedLogin();

        eng.IsLockedOut().Should().BeTrue();
        eng.LockedUntil.Should().NotBeNull();
        eng.LockedUntil!.Value.Should().BeAfter(DateTime.UtcNow.AddMinutes(14));
    }

    [Fact]
    public void IsLockedOut_returns_false_before_threshold()
    {
        var eng = NewEngineer();
        eng.RecordFailedLogin();
        eng.RecordFailedLogin();

        eng.IsLockedOut().Should().BeFalse();
    }

    [Fact]
    public void IsLockedOut_returns_false_when_lock_has_expired()
    {
        var eng = NewEngineer();
        for (var i = 0; i < 5; i++) eng.RecordFailedLogin();

        // Manually wind the lock back into the past
        typeof(Engineer).GetProperty("LockedUntil")!.SetValue(eng, DateTime.UtcNow.AddMinutes(-1));

        eng.IsLockedOut().Should().BeFalse();
    }

    [Fact]
    public void ResetLoginAttempts_clears_counter_and_lock()
    {
        var eng = NewEngineer();
        for (var i = 0; i < 5; i++) eng.RecordFailedLogin();

        eng.ResetLoginAttempts();

        eng.FailedLoginAttempts.Should().Be(0);
        eng.LockedUntil.Should().BeNull();
        eng.IsLockedOut().Should().BeFalse();
    }

    [Fact]
    public void UnlockAccount_clears_counter_and_lock()
    {
        var eng = NewEngineer();
        for (var i = 0; i < 5; i++) eng.RecordFailedLogin();

        eng.UnlockAccount();

        eng.FailedLoginAttempts.Should().Be(0);
        eng.LockedUntil.Should().BeNull();
        eng.IsLockedOut().Should().BeFalse();
    }

    // ── Role ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Roles.Engineer)]
    [InlineData(Roles.TeamLead)]
    [InlineData(Roles.Designer)]
    [InlineData(Roles.ProductManager)]
    [InlineData(Roles.ProjectManager)]
    [InlineData(Roles.HeadOfRnD)]
    [InlineData(Roles.HeadOfProduct)]
    [InlineData(Roles.HeadOfDesign)]
    [InlineData(Roles.HeadOfPmo)]
    [InlineData(Roles.HeadOfFunctional)]
    [InlineData(Roles.HeadOfCoreBanking)]
    [InlineData(Roles.HeadOfInfraDevOps)]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    public void UpdateRole_accepts_all_valid_roles(string role)
    {
        var eng = NewEngineer();

        eng.UpdateRole(role);

        eng.Role.Should().Be(role);
    }

    [Fact]
    public void UpdateRole_throws_DomainException_for_invalid_role()
    {
        var eng = NewEngineer();

        var act = () => eng.UpdateRole("superuser");

        act.Should().Throw<DomainException>().WithMessage("*not a valid role*");
    }

    // ── Active / Deactivate ───────────────────────────────────────────────────

    [Fact]
    public void Deactivate_sets_IsActive_false()
    {
        var eng = NewEngineer();

        eng.Deactivate();

        eng.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Reactivate_sets_IsActive_true()
    {
        var eng = NewEngineer();
        eng.Deactivate();

        eng.Reactivate();

        eng.IsActive.Should().BeTrue();
    }

    // ── Password reset ────────────────────────────────────────────────────────

    [Fact]
    public void SetPasswordResetToken_stores_token_and_expiry()
    {
        var eng = NewEngineer();
        var expiry = DateTime.UtcNow.AddHours(1);

        eng.SetPasswordResetToken("tok123", expiry);

        eng.PasswordResetToken.Should().Be("tok123");
        eng.PasswordResetTokenExpiresAt.Should().Be(expiry);
    }

    [Fact]
    public void SetPasswordHash_updates_hash_and_clears_token()
    {
        var eng = NewEngineer();
        eng.SetPasswordResetToken("tok123", DateTime.UtcNow.AddHours(1));

        eng.SetPasswordHash("new_hash");

        eng.PasswordHash.Should().Be("new_hash");
        eng.PasswordResetToken.Should().BeNull();
        eng.PasswordResetTokenExpiresAt.Should().BeNull();
    }

    // ── Baseline ─────────────────────────────────────────────────────────────

    [Fact]
    public void UpdateBaseline_changes_points_and_cycle_days()
    {
        var eng = NewEngineer();

        eng.UpdateBaseline(40, 21);

        eng.BaselinePoints.Should().Be(40);
        eng.BaselineCycleDays.Should().Be(21);
    }

    [Theory]
    [InlineData(0, 14)]
    [InlineData(-5, 14)]
    [InlineData(20, 0)]
    [InlineData(20, -1)]
    public void Create_rejects_non_positive_baseline(int points, int cycleDays)
    {
        var act = () => Engineer.Create("Dev User", "dev@pulse.io", "hashed_pw", Roles.Engineer, points, cycleDays);

        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(0, 14)]
    [InlineData(20, 0)]
    public void UpdateBaseline_rejects_non_positive_values(int points, int cycleDays)
    {
        var eng = NewEngineer();

        var act = () => eng.UpdateBaseline(points, cycleDays);

        act.Should().Throw<DomainException>();
        eng.BaselinePoints.Should().Be(20, "the rejected update must not have applied");
    }

    // ── Team assignment ───────────────────────────────────────────────────────

    [Fact]
    public void AssignToTeam_sets_TeamId()
    {
        var eng = NewEngineer();
        var teamId = Guid.NewGuid();

        eng.AssignToTeam(teamId);

        eng.TeamId.Should().Be(teamId);
    }

    [Fact]
    public void AssignToTeam_null_clears_TeamId()
    {
        var eng = NewEngineer();
        eng.AssignToTeam(Guid.NewGuid());

        eng.AssignToTeam(null);

        eng.TeamId.Should().BeNull();
    }
}
