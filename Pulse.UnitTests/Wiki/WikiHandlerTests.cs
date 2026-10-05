using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects;
using Pulse.Application.Projects.Commands;
using Pulse.Application.Projects.Queries;
using Pulse.Domain.Projects;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Wiki;

public class WikiHandlerTests
{
    private readonly Mock<IWikiRepository> _wiki = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    public WikiHandlerTests()
    {
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private static Project AnyProject() => Project.Create("Test project");

    private static WikiPage AnyPage(Guid? projectId = null)
    {
        var pid = projectId ?? Guid.NewGuid();
        return WikiPage.Create(pid, "Getting started", "# Hello", Guid.NewGuid());
    }

    // ── ListWikiPagesHandler ──────────────────────────────────────────────────

    [Fact]
    public async Task ListWikiPages_returns_NOT_FOUND_when_project_missing()
    {
        _projects.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((Project?)null);

        var result = await new ListWikiPagesHandler(_wiki.Object, _projects.Object, _access.Object)
            .Handle(new ListWikiPagesQuery(Guid.NewGuid(), Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task ListWikiPages_returns_summaries_for_existing_project()
    {
        var project = AnyProject();
        var page = AnyPage(project.Id);
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _wiki.Setup(r => r.ListByProjectPagedAsync(project.Id, It.IsAny<int>(), null, default))
            .ReturnsAsync(new[] { page });

        var result = await new ListWikiPagesHandler(_wiki.Object, _projects.Object, _access.Object)
            .Handle(new ListWikiPagesQuery(project.Id, Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Should().HaveCount(1);
        result.Data.Items[0].Title.Should().Be(page.Title);
    }

    // ── GetWikiPageHandler ────────────────────────────────────────────────────

    [Fact]
    public async Task GetWikiPage_returns_NOT_FOUND_when_page_missing()
    {
        _wiki.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((WikiPage?)null);

        var result = await new GetWikiPageHandler(_wiki.Object, _access.Object)
            .Handle(new GetWikiPageQuery(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GetWikiPage_returns_NOT_FOUND_when_project_id_mismatch()
    {
        var page = AnyPage();
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);

        var result = await new GetWikiPageHandler(_wiki.Object, _access.Object)
            .Handle(new GetWikiPageQuery(Guid.NewGuid(), page.Id, Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GetWikiPage_returns_page_dto()
    {
        var page = AnyPage();
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);

        var result = await new GetWikiPageHandler(_wiki.Object, _access.Object)
            .Handle(new GetWikiPageQuery(page.ProjectId, page.Id, Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Title.Should().Be(page.Title);
        result.Data.Content.Should().Be(page.Content);
    }

    // ── CreateWikiPageHandler ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateWikiPage_returns_NOT_FOUND_when_project_missing()
    {
        _projects.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((Project?)null);

        var result = await new CreateWikiPageHandler(_wiki.Object, _projects.Object, _access.Object)
            .Handle(new CreateWikiPageCommand(Guid.NewGuid(), "Title", "Content", Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task CreateWikiPage_persists_and_returns_dto()
    {
        var project = AnyProject();
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);

        var actorId = Guid.NewGuid();
        var result = await new CreateWikiPageHandler(_wiki.Object, _projects.Object, _access.Object)
            .Handle(new CreateWikiPageCommand(project.Id, "Architecture", "## Overview", actorId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Title.Should().Be("Architecture");
        result.Data.Content.Should().Be("## Overview");
        result.Data.AuthorId.Should().Be(actorId);
        _wiki.Verify(r => r.AddAsync(It.IsAny<WikiPage>(), default), Times.Once);
        _wiki.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }

    // ── UpdateWikiPageHandler ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateWikiPage_returns_NOT_FOUND_when_page_missing()
    {
        _wiki.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((WikiPage?)null);

        var result = await new UpdateWikiPageHandler(_wiki.Object, _access.Object)
            .Handle(new UpdateWikiPageCommand(Guid.NewGuid(), Guid.NewGuid(), "T", "C", Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task UpdateWikiPage_saves_revision_before_updating()
    {
        var page = AnyPage();
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);
        var originalTitle = page.Title;
        var originalContent = page.Content;

        await new UpdateWikiPageHandler(_wiki.Object, _access.Object)
            .Handle(new UpdateWikiPageCommand(page.ProjectId, page.Id, "New title", "New content", Guid.NewGuid()), default);

        _wiki.Verify(r => r.AddRevisionAsync(
            It.Is<WikiPageRevision>(rev => rev.Title == originalTitle && rev.Content == originalContent),
            default), Times.Once);
    }

    [Fact]
    public async Task UpdateWikiPage_applies_new_title_and_content()
    {
        var page = AnyPage();
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);

        var result = await new UpdateWikiPageHandler(_wiki.Object, _access.Object)
            .Handle(new UpdateWikiPageCommand(page.ProjectId, page.Id, "New title", "New content", Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        page.Title.Should().Be("New title");
        page.Content.Should().Be("New content");
        page.UpdatedAt.Should().NotBeNull();
    }

    // ── DeleteWikiPageHandler ─────────────────────────────────────────────────

    [Fact]
    public async Task DeleteWikiPage_returns_NOT_FOUND_when_page_missing()
    {
        _wiki.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((WikiPage?)null);

        var result = await new DeleteWikiPageHandler(_wiki.Object, _access.Object)
            .Handle(new DeleteWikiPageCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task DeleteWikiPage_removes_page_and_saves()
    {
        var page = AnyPage();
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);

        var result = await new DeleteWikiPageHandler(_wiki.Object, _access.Object)
            .Handle(new DeleteWikiPageCommand(page.ProjectId, page.Id, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        _wiki.Verify(r => r.Remove(page), Times.Once);
        _wiki.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }

    // ── ListWikiRevisionsHandler ──────────────────────────────────────────────

    [Fact]
    public async Task ListWikiRevisions_returns_NOT_FOUND_when_page_missing()
    {
        _wiki.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((WikiPage?)null);

        var result = await new ListWikiRevisionsHandler(_wiki.Object, _access.Object)
            .Handle(new ListWikiRevisionsQuery(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task ListWikiRevisions_returns_NOT_FOUND_on_project_mismatch()
    {
        var page = AnyPage();
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);

        var result = await new ListWikiRevisionsHandler(_wiki.Object, _access.Object)
            .Handle(new ListWikiRevisionsQuery(Guid.NewGuid(), page.Id, Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task ListWikiRevisions_returns_revision_summaries()
    {
        var page = AnyPage();
        var revision = WikiPageRevision.Create(page.Id, "Old title", "Old content", Guid.NewGuid());
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);
        _wiki.Setup(r => r.GetRevisionsAsync(page.Id, default))
            .ReturnsAsync(new[] { revision });

        var result = await new ListWikiRevisionsHandler(_wiki.Object, _access.Object)
            .Handle(new ListWikiRevisionsQuery(page.ProjectId, page.Id, Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(1);
        result.Data![0].Title.Should().Be("Old title");
    }

    // ── GetWikiRevisionHandler ────────────────────────────────────────────────

    [Fact]
    public async Task GetWikiRevision_returns_NOT_FOUND_when_page_missing()
    {
        _wiki.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((WikiPage?)null);

        var result = await new GetWikiRevisionHandler(_wiki.Object, _access.Object)
            .Handle(new GetWikiRevisionQuery(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GetWikiRevision_returns_NOT_FOUND_when_revision_missing()
    {
        var page = AnyPage();
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);
        _wiki.Setup(r => r.GetRevisionByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((WikiPageRevision?)null);

        var result = await new GetWikiRevisionHandler(_wiki.Object, _access.Object)
            .Handle(new GetWikiRevisionQuery(page.ProjectId, page.Id, Guid.NewGuid(), Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GetWikiRevision_returns_NOT_FOUND_when_revision_belongs_to_different_page()
    {
        var page = AnyPage();
        var orphanRevision = WikiPageRevision.Create(Guid.NewGuid(), "Title", "Content", Guid.NewGuid());
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);
        _wiki.Setup(r => r.GetRevisionByIdAsync(orphanRevision.Id, default)).ReturnsAsync(orphanRevision);

        var result = await new GetWikiRevisionHandler(_wiki.Object, _access.Object)
            .Handle(new GetWikiRevisionQuery(page.ProjectId, page.Id, orphanRevision.Id, Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GetWikiRevision_returns_full_content()
    {
        var page = AnyPage();
        var revision = WikiPageRevision.Create(page.Id, "Old title", "Old content", Guid.NewGuid());
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);
        _wiki.Setup(r => r.GetRevisionByIdAsync(revision.Id, default)).ReturnsAsync(revision);

        var result = await new GetWikiRevisionHandler(_wiki.Object, _access.Object)
            .Handle(new GetWikiRevisionQuery(page.ProjectId, page.Id, revision.Id, Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Title.Should().Be("Old title");
        result.Data.Content.Should().Be("Old content");
    }

    // ── ListAllWikiPagesHandler ───────────────────────────────────────────────

    [Fact]
    public async Task ListAllWikiPages_returns_empty_list_when_no_pages()
    {
        _wiki.Setup(r => r.ListAllPagedAsync(It.IsAny<int>(), null, default))
            .ReturnsAsync(Array.Empty<WikiIndexEntryDto>());

        var result = await new ListAllWikiPagesHandler(_wiki.Object, _access.Object)
            .Handle(new ListAllWikiPagesQuery(Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAllWikiPages_returns_all_entries()
    {
        var entries = new[]
        {
            new WikiIndexEntryDto(Guid.NewGuid(), "Intro", Guid.NewGuid(), "Alpha", DateTime.UtcNow, null),
            new WikiIndexEntryDto(Guid.NewGuid(), "Setup", Guid.NewGuid(), "Beta",  DateTime.UtcNow, null),
        };
        _wiki.Setup(r => r.ListAllPagedAsync(It.IsAny<int>(), null, default)).ReturnsAsync(entries);

        var result = await new ListAllWikiPagesHandler(_wiki.Object, _access.Object)
            .Handle(new ListAllWikiPagesQuery(Guid.NewGuid(), ""), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Should().HaveCount(2);
        result.Data.Items[0].PageTitle.Should().Be("Intro");
        result.Data.Items[1].ProjectName.Should().Be("Beta");
    }

    [Fact]
    public async Task ListAllWikiPages_lists_every_open_page_but_hides_restricted_ones_from_non_members()
    {
        var memberProject = Guid.NewGuid();
        var otherProject = Guid.NewGuid();
        var entries = new[]
        {
            new WikiIndexEntryDto(Guid.NewGuid(), "Open elsewhere",       otherProject,  "Beta",  DateTime.UtcNow, null),
            new WikiIndexEntryDto(Guid.NewGuid(), "Restricted elsewhere", otherProject,  "Beta",  DateTime.UtcNow, null, RestrictedToMembers: true),
            new WikiIndexEntryDto(Guid.NewGuid(), "Restricted, mine",     memberProject, "Alpha", DateTime.UtcNow, null, RestrictedToMembers: true),
        };
        _wiki.Setup(r => r.ListAllPagedAsync(It.IsAny<int>(), null, default)).ReturnsAsync(entries);
        _access
            .Setup(a => a.CanAccessProjectAsync(otherProject, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await new ListAllWikiPagesHandler(_wiki.Object, _access.Object)
            .Handle(new ListAllWikiPagesQuery(Guid.NewGuid(), "engineer"), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Select(e => e.PageTitle).Should().BeEquivalentTo("Open elsewhere", "Restricted, mine");
    }

    [Fact]
    public async Task ListAllWikiPages_shows_restricted_pages_to_the_org_read_only_roles()
    {
        var entries = new[]
        {
            new WikiIndexEntryDto(Guid.NewGuid(), "Restricted", Guid.NewGuid(), "Beta", DateTime.UtcNow, null, RestrictedToMembers: true),
        };
        _wiki.Setup(r => r.ListAllPagedAsync(It.IsAny<int>(), null, default)).ReturnsAsync(entries);
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await new ListAllWikiPagesHandler(_wiki.Object, _access.Object)
            .Handle(new ListAllWikiPagesQuery(Guid.NewGuid(), "executive"), default);

        result.Data!.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task ListAllWikiPages_hasMore_reflects_underlying_rows_not_the_post_filter_visible_count()
    {
        // A page of `limit` raw rows where one is a restricted page the caller can't see should
        // still report hasMore based on whether more raw rows exist — a short visible page must
        // not be mistaken for "that was the last page" by the caller paging through results.
        var restrictedProject = Guid.NewGuid();
        var entries = new[]
        {
            new WikiIndexEntryDto(Guid.NewGuid(), "Visible", Guid.NewGuid(), "Alpha", DateTime.UtcNow, null),
            new WikiIndexEntryDto(Guid.NewGuid(), "Hidden from me", restrictedProject, "Beta", DateTime.UtcNow, null, RestrictedToMembers: true),
        };
        _wiki.Setup(r => r.ListAllPagedAsync(2, null, default)).ReturnsAsync(entries); // limit=1 + lookahead
        _access
            .Setup(a => a.CanAccessProjectAsync(restrictedProject, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await new ListAllWikiPagesHandler(_wiki.Object, _access.Object)
            .Handle(new ListAllWikiPagesQuery(Guid.NewGuid(), "engineer", Limit: 1), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.HasMore.Should().BeTrue();
        result.Data.Items.Should().ContainSingle(e => e.PageTitle == "Visible");
    }

    // ── Open read: GetWikiPage ────────────────────────────────────────────────

    [Fact]
    public async Task GetWikiPage_open_page_is_readable_by_a_non_member()
    {
        var page = AnyPage();
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);
        _access
            .Setup(a => a.CanAccessProjectAsync(page.ProjectId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await new GetWikiPageHandler(_wiki.Object, _access.Object)
            .Handle(new GetWikiPageQuery(page.ProjectId, page.Id, Guid.NewGuid(), "engineer"), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetWikiPage_restricted_page_is_refused_to_a_non_member_but_open_to_a_member()
    {
        var page = WikiPage.Create(Guid.NewGuid(), "Secrets", "x", Guid.NewGuid(), restrictedToMembers: true);
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);
        var member = Guid.NewGuid();
        _access
            .Setup(a => a.CanAccessProjectAsync(page.ProjectId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _access
            .Setup(a => a.CanAccessProjectAsync(page.ProjectId, member, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var handler = new GetWikiPageHandler(_wiki.Object, _access.Object);

        var outsider = await handler.Handle(new GetWikiPageQuery(page.ProjectId, page.Id, Guid.NewGuid(), "engineer"), default);
        var insider = await handler.Handle(new GetWikiPageQuery(page.ProjectId, page.Id, member, "engineer"), default);

        outsider.ErrorCode.Should().Be("FORBIDDEN");
        insider.IsSuccess.Should().BeTrue();
        insider.Data!.RestrictedToMembers.Should().BeTrue();
    }

    // ── Restricting a page ────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateWikiPage_changes_the_restriction_only_when_one_is_given()
    {
        var page = AnyPage();
        _wiki.Setup(r => r.GetByIdAsync(page.Id, default)).ReturnsAsync(page);
        var handler = new UpdateWikiPageHandler(_wiki.Object, _access.Object);

        await handler.Handle(new UpdateWikiPageCommand(page.ProjectId, page.Id, "T", "C", Guid.NewGuid(), "project_manager", true), default);
        page.RestrictedToMembers.Should().BeTrue();

        await handler.Handle(new UpdateWikiPageCommand(page.ProjectId, page.Id, "T2", "C2", Guid.NewGuid(), "project_manager"), default);
        page.RestrictedToMembers.Should().BeTrue("leaving the field out must not silently re-open a restricted page");

        await handler.Handle(new UpdateWikiPageCommand(page.ProjectId, page.Id, "T3", "C3", Guid.NewGuid(), "project_manager", false), default);
        page.RestrictedToMembers.Should().BeFalse();
    }
}
