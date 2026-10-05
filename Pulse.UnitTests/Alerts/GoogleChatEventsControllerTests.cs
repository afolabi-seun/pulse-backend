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

    public GoogleChatEventsControllerTests()
    {
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("1");
    }

    private GoogleChatEventsController CreateController(string? bearerToken)
    {
        var ctx = new DefaultHttpContext();
        if (bearerToken is not null) ctx.Request.Headers.Authorization = $"Bearer {bearerToken}";

        return new GoogleChatEventsController(_verifier.Object, _spaces.Object, _jobs.Object)
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
}
