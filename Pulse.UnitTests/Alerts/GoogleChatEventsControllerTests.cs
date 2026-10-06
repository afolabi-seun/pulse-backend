using Pulse.Application.Notifications.Preferences;
using Pulse.Application.Integrations.GoogleChat;
using Pulse.Application.Common;
using MediatR;
using System.Text.Json.Nodes;
using Pulse.Api.Controllers;
using Pulse.Application.Common.Interfaces;
using Pulse.Infrastructure.GoogleChat;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Pulse.UnitTests.Alerts;

public class GoogleChatEventsControllerTests
{
    private readonly Mock<IGoogleChatRequestVerifier> _verifier = new();
    private readonly Mock<IGoogleChatSpaceRepository> _spaces = new();
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly Mock<IMediator> _mediator = new();

    public GoogleChatEventsControllerTests()
    {
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("1");
    }

    private GoogleChatEventsController CreateController(string? bearerToken)
    {
        var ctx = new DefaultHttpContext();
        if (bearerToken is not null) ctx.Request.Headers.Authorization = $"Bearer {bearerToken}";

        return new GoogleChatEventsController(_verifier.Object, _spaces.Object, _jobs.Object, _mediator.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };
    }

    private static JsonObject AddedToSpaceEvent(string spaceId, string displayName) => new()
    {
        ["type"] = "ADDED_TO_SPACE",
        ["space"] = new JsonObject { ["name"] = spaceId, ["displayName"] = displayName },
    };

    private static JsonObject MessageEvent(string spaceId, string threadName, string text, bool fromBot = false) => new()
    {
        ["type"] = "MESSAGE",
        ["space"] = new JsonObject { ["name"] = spaceId },
        ["message"] = new JsonObject
        {
            ["text"] = text,
            ["thread"] = new JsonObject { ["name"] = threadName },
            ["sender"] = new JsonObject { ["type"] = fromBot ? "BOT" : "HUMAN" },
        },
    };

    [Fact]
    public async Task Rejects_a_request_with_no_bearer_token()
    {
        var result = await CreateController(null).Receive(AddedToSpaceEvent("spaces/A", "Team"), default);

        result.Should().BeOfType<UnauthorizedResult>();
        _verifier.Verify(v => v.VerifyAsync(null, default), Times.Once);
    }

    [Fact]
    public async Task Rejects_a_request_the_verifier_rejects()
    {
        _verifier.Setup(v => v.VerifyAsync("bad-token", default)).ReturnsAsync(false);

        var result = await CreateController("bad-token").Receive(AddedToSpaceEvent("spaces/A", "Team"), default);

        result.Should().BeOfType<UnauthorizedResult>();
        _spaces.Verify(s => s.UpsertAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task Upserts_the_space_on_ADDED_TO_SPACE()
    {
        _verifier.Setup(v => v.VerifyAsync("good-token", default)).ReturnsAsync(true);

        var result = await CreateController("good-token").Receive(AddedToSpaceEvent("spaces/A", "Team Falcon"), default);

        result.Should().BeOfType<OkObjectResult>();
        _spaces.Verify(s => s.UpsertAsync("spaces/A", "Team Falcon", default), Times.Once);
        _spaces.Verify(s => s.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task Enqueues_the_reply_job_for_a_genuine_human_message()
    {
        _verifier.Setup(v => v.VerifyAsync("good-token", default)).ReturnsAsync(true);
        var evt = MessageEvent("spaces/A", "spaces/A/threads/1", "why though?");

        var result = await CreateController("good-token").Receive(evt, default);

        result.Should().BeOfType<OkObjectResult>();
        _jobs.Verify(j => j.Create(
            It.Is<Job>(job => job.Method.Name == nameof(HandleGoogleChatReplyJob.ExecuteAsync)
                && (string)job.Args[0] == "spaces/A"
                && (string)job.Args[1] == "spaces/A/threads/1"
                && (string)job.Args[2] == "why though?"),
            It.IsAny<IState>()), Times.Once);
    }

    [Fact]
    public async Task Ignores_a_message_from_the_bot_itself()
    {
        _verifier.Setup(v => v.VerifyAsync("good-token", default)).ReturnsAsync(true);
        var evt = MessageEvent("spaces/A", "spaces/A/threads/1", "an alert fired", fromBot: true);

        var result = await CreateController("good-token").Receive(evt, default);

        result.Should().BeOfType<OkObjectResult>();
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }
    // ── Linking a space (multi-tenancy Phase 2c) ─────────────────────────────

    private static JsonObject MessageBody(string argumentText) => new()
    {
        ["type"] = "MESSAGE",
        ["space"] = new JsonObject { ["name"] = "spaces/A", ["displayName"] = "Alerts" },
        ["message"] = new JsonObject
        {
            ["text"] = "@Pulse " + argumentText,
            ["argumentText"] = argumentText,
            ["thread"] = new JsonObject { ["name"] = "spaces/A/threads/1" },
        },
    };

    [Theory]
    [InlineData("link ABCD-2345", "ABCD-2345")]
    [InlineData("  LINK abcd2345 ", "abcd2345")]
    public async Task A_link_command_links_the_space_and_replies_in_it(string argumentText, string expectedCode)
    {
        _verifier.Setup(v => v.VerifyAsync("good", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mediator.Setup(m => m.Send(It.IsAny<LinkGoogleChatSpaceCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<string>.Ok("Linked this space to Acme."));

        var result = await CreateController("good").Receive(MessageBody(argumentText), default);

        _mediator.Verify(m => m.Send(It.Is<LinkGoogleChatSpaceCommand>(c =>
            c.SpaceId == "spaces/A" && c.DisplayName == "Alerts" && c.Code == expectedCode), It.IsAny<CancellationToken>()));
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(new { text = "Linked this space to Acme." });
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never, "a link command isn't a follow-up question");
    }

    [Fact]
    public async Task An_ordinary_message_mentioning_link_is_not_a_link_command()
    {
        _verifier.Setup(v => v.VerifyAsync("good", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await CreateController("good").Receive(MessageBody("can you link me to the dashboard?"), default);

        _mediator.Verify(m => m.Send(It.IsAny<LinkGoogleChatSpaceCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    // ── Direct messages (personal notifications) ─────────────────────────────

    [Theory]
    [InlineData("ADDED_TO_SPACE")]
    [InlineData("MESSAGE")]
    public async Task An_event_in_a_direct_message_links_it_to_the_persons_account(string type)
    {
        _verifier.Setup(v => v.VerifyAsync("good", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mediator.Setup(m => m.Send(It.IsAny<LinkGoogleChatDirectMessageCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<string>.Ok("Hi Ada!"));
        var body = new JsonObject
        {
            ["type"] = type,
            ["space"] = new JsonObject { ["name"] = "spaces/DM1", ["type"] = "DM" },
            ["user"] = new JsonObject { ["email"] = "ada@acme.test" },
            ["message"] = new JsonObject { ["text"] = "hello", ["thread"] = new JsonObject { ["name"] = "spaces/DM1/threads/1" } },
        };

        var result = await CreateController("good").Receive(body, default);

        _mediator.Verify(m => m.Send(It.Is<LinkGoogleChatDirectMessageCommand>(c => c.Email == "ada@acme.test" && c.DmSpace == "spaces/DM1"),
            It.IsAny<CancellationToken>()));
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(new { text = "Hi Ada!" });
        _spaces.Verify(s => s.UpsertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "a DM isn't an organization space");
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }
}
