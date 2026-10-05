using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Users;
using Pulse.Application.Users.Commands;
using Pulse.Application.Users.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/users")]
[Tags("Users")]
public class UsersController : ControllerBase
{
    private readonly IMediator _mediator;

    public UsersController(IMediator mediator) => _mediator = mediator;

    public record CreateUserRequest(string Name, string Email, string Role, int BaselinePoints, int BaselineCycleDays, Guid? TeamId, bool IsQa = false, string? Discipline = null);
    public record UpdateUserRequest(string? Role, bool? IsActive, bool? UnlockAccount, Guid? TeamId, bool? IsQa = null, string? Discipline = null);

    /// <summary>Returns all users including inactive accounts, ordered by name.</summary>
    [HttpGet]
    [RequiresCapability(CapabilityRegistry.PmOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead)]
    [ProducesResponseType(typeof(ApiResponse<PagedResultDto<UserDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListUsers(
        [FromQuery] int limit = 25,
        [FromQuery] string? cursor = null,
        [FromQuery] string? role = null,
        [FromQuery] string? team = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] bool? isQa = null)
    {
        var callerRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        var callerId   = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return (await _mediator.Send(new ListUsersQuery(limit, cursor, callerRole, callerId, role, team, isActive, isQa))).ToActionResult();
    }

    /// <summary>Creates a new user account and sends an activation email.</summary>
    /// <remarks>
    /// Roles.UserManagementGlobalRoles (Head of PMO, Project Manager, Head of Product) can create
    /// any role. Other department heads can only create roles within their own department and
    /// cannot create head-level roles.
    /// A cryptographically random temporary password is assigned. The user must set their own
    /// password via the activation link (valid 24 hours) before they can log in.
    /// </remarks>
    [HttpPost]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
    {
        var callerRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        var isPmo = Roles.UserManagementGlobalRoles.Contains(callerRole);

        if (!isPmo)
        {
            var allowed = Roles.CreatableByDeptHead(callerRole);
            if (!allowed.Contains(request.Role))
                return StatusCode(StatusCodes.Status403Forbidden,
                    ApiResponse<object>.Failure("FORBIDDEN",
                        "You can only create users with roles in your department."));
        }

        Discipline? discipline = request.Discipline is not null && Enum.TryParse<Discipline>(request.Discipline, ignoreCase: true, out var parsedDisc)
            ? parsedDisc : null;

        var result = await _mediator.Send(new CreateUserCommand(
            request.Name, request.Email, request.Role,
            request.BaselinePoints, request.BaselineCycleDays,
            request.TeamId, request.IsQa, discipline, GetActorId(), GetIp()));

        return result.ToCreatedResult("GetUser", new { id = result.Data?.Id });
    }

    public record CheckEmailsRequest(IReadOnlyList<string> Emails);

    /// <summary>Checks which of the given emails already have an account. For vetting a batch of
    /// candidate invitees without needing direct database access.</summary>
    [HttpPost("check-emails")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EmailExistsDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CheckEmails([FromBody] CheckEmailsRequest request) =>
        (await _mediator.Send(new CheckEmailsExistQuery(request.Emails))).ToActionResult();

    /// <summary>Returns a single user by ID.</summary>
    [HttpGet("{id:guid}", Name = "GetUser")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead)]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetUser(Guid id) =>
        (await _mediator.Send(new GetUserQuery(id))).ToActionResult();

    /// <summary>Partially updates a user — role, active status, or account unlock.</summary>
    /// <remarks>
    /// Role changes follow the same dept-scoping as user creation: non-PMO dept heads
    /// can only assign roles within their own department.
    /// </remarks>
    [HttpPatch("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserRequest request)
    {
        if (request.Role is not null)
        {
            var callerRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
            var isPmo = Roles.UserManagementGlobalRoles.Contains(callerRole);
            if (!isPmo)
            {
                var allowed = Roles.CreatableByDeptHead(callerRole);
                if (!allowed.Contains(request.Role))
                    return StatusCode(StatusCodes.Status403Forbidden,
                        ApiResponse<object>.Failure("FORBIDDEN", "You can only assign roles in your department."));
            }
        }

        Discipline? discipline = request.Discipline is not null && Enum.TryParse<Discipline>(request.Discipline, ignoreCase: true, out var parsedDisc)
            ? parsedDisc : null;

        return (await _mediator.Send(new UpdateUserCommand(
            id, request.Role, request.IsActive, request.UnlockAccount, request.TeamId,
            request.IsQa, discipline, GetActorId(), GetIp()))).ToActionResult();
    }

    /// <summary>Resends the account activation email to a user with a fresh link.</summary>
    [HttpPost("{id:guid}/resend-invite")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ResendInvite(Guid id)
    {
        var result = await _mediator.Send(new ResendInviteCommand(id, GetActorId(), GetIp()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    /// <summary>Soft-deletes a user by deactivating their account. All historical data is preserved.</summary>
    /// <remarks>
    /// Roles.UserManagementGlobalRoles (Head of PMO, Project Manager, Head of Product) can
    /// deactivate any user. Other department heads can only deactivate users whose role falls
    /// within their own department.
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteUser(Guid id)
    {
        var callerRole = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
        var isPmo = Roles.UserManagementGlobalRoles.Contains(callerRole);

        if (!isPmo)
        {
            var target = (await _mediator.Send(new GetUserQuery(id))).Data;
            if (target is null)
                return NotFound(ApiResponse<object>.Failure("NOT_FOUND", "User not found."));

            var allowed = Roles.CreatableByDeptHead(callerRole);
            if (!allowed.Contains(target.Role))
                return StatusCode(StatusCodes.Status403Forbidden,
                    ApiResponse<object>.Failure("FORBIDDEN", "You can only deactivate users in your department."));
        }

        var result = await _mediator.Send(new DeleteUserCommand(id, GetActorId(), GetIp()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
