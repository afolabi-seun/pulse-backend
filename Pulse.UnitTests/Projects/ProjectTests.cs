using Pulse.Domain.Common;
using Pulse.Domain.Projects;
using FluentAssertions;

namespace Pulse.UnitTests.Projects;

public class ProjectTests
{
    private static Project NewProject() => Project.Create("Test project");

    [Fact]
    public void Create_defaults_to_Active()
    {
        var project = NewProject();

        project.Status.Should().Be(ProjectStatus.Active);
    }

    [Fact]
    public void Archive_sets_status_to_Archived()
    {
        var project = NewProject();

        project.Archive();

        project.Status.Should().Be(ProjectStatus.Archived);
    }

    [Fact]
    public void Pause_sets_status_to_Paused()
    {
        var project = NewProject();

        project.Pause();

        project.Status.Should().Be(ProjectStatus.Paused);
    }

    [Fact]
    public void Pause_on_already_paused_project_throws_DomainException()
    {
        var project = NewProject();
        project.Pause();

        var act = () => project.Pause();

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Pause_on_archived_project_throws_DomainException()
    {
        var project = NewProject();
        project.Archive();

        var act = () => project.Pause();

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Resume_sets_status_to_Active()
    {
        var project = NewProject();
        project.Pause();

        project.Resume();

        project.Status.Should().Be(ProjectStatus.Active);
    }

    [Fact]
    public void Resume_on_active_project_throws_DomainException()
    {
        var project = NewProject();

        var act = () => project.Resume();

        act.Should().Throw<DomainException>().WithMessage("*not paused*");
    }

    // ── Code ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_with_an_explicit_code_uses_it_verbatim()
    {
        var project = Project.Create("Notifications Platform", code: "NOTIF");

        project.Code.Should().Be("NOTIF");
    }

    [Fact]
    public void Create_with_no_code_derives_one_from_the_name()
    {
        var project = Project.Create("Notifications Platform");

        project.Code.Should().Be("NOTIFI");
    }

    [Fact]
    public void Create_with_a_name_with_no_letters_or_digits_falls_back_to_PROJ()
    {
        var project = Project.Create("---");

        project.Code.Should().Be("PROJ");
    }

    [Theory]
    [InlineData("notif")]
    [InlineData("N")]
    [InlineData("N0TIF-1")]
    [InlineData("TOOLONGCODEXX")]
    public void Create_rejects_a_malformed_explicit_code(string badCode)
    {
        var act = () => Project.Create("Test project", code: badCode);

        act.Should().Throw<DomainException>().WithMessage("*Project code*");
    }

    [Fact]
    public void SetCode_replaces_the_code()
    {
        var project = NewProject();

        project.SetCode("NEWCODE");

        project.Code.Should().Be("NEWCODE");
    }

    [Fact]
    public void SetCode_rejects_a_malformed_code()
    {
        var project = NewProject();

        var act = () => project.SetCode("nope");

        act.Should().Throw<DomainException>().WithMessage("*Project code*");
    }
}
