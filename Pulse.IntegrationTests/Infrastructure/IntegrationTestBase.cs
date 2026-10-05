using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Projects;
using Pulse.Domain.Sprints;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using Pulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Infrastructure;

public abstract class IntegrationTestBase
{
    protected readonly HttpClient Client;
    protected readonly PulseWebApplicationFactory Factory;
    protected static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    protected IntegrationTestBase(PulseWebApplicationFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient();
    }

    protected async Task<Engineer> SeedEngineerAsync(string email, string role = Roles.Engineer, bool isQa = false)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<Application.Common.Interfaces.IPasswordHasher>();
        var engineer = Engineer.Create("Test User", email, hasher.Hash("Str0ng!Pass12"), role, 20, 14);
        if (isQa) engineer.SetIsQa(true);
        db.Engineers.Add(engineer);
        await db.SaveChangesAsync();
        return engineer;
    }

    protected async Task<string> LoginTokenAsync(string email)
    {
        var resp = await Client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Str0ng!Pass12" });
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<AuthDto>>(JsonOpts);
        return body!.Data!.AccessToken;
    }

    protected HttpClient AuthenticatedClient(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected async Task<HttpClient> AuthenticatedClientAsync(string email)
    {
        var token = await LoginTokenAsync(email);
        return AuthenticatedClient(token);
    }

    protected async Task<Project> SeedProjectAsync(string name = "Test Project", Guid? ownerTeamId = null, string? code = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        // Project.Create's own name-derived fallback code isn't unique across calls — most tests
        // reuse the default "Test Project" name, which would collide on Code's unique DB index
        // the moment a second test seeds one. A per-call random code sidesteps that entirely; its
        // value is never asserted on, only that it exists — unless a caller passes one explicitly
        // (e.g. to assert on a resulting task key), which takes precedence.
        var project = Project.Create(name, ownerTeamId: ownerTeamId, code: code ?? $"T{Guid.NewGuid():N}"[..8].ToUpperInvariant());
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    protected async Task SeedProjectMemberAsync(Guid projectId, Guid engineerId)
    {
        using var scope = Factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.AddMemberAsync(projectId, engineerId);
        await projects.SaveChangesAsync();
    }

    protected async Task<Team> SeedTeamAsync(string name = "Test Team", Guid? teamLeadId = null, string? department = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var team = Team.Create(name, teamLeadId, department);
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        return team;
    }

    protected async Task<Notification> SeedNotificationAsync(Guid userId, string kind = "checkin_reminder")
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var notification = Notification.Create(userId, kind);
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();
        return notification;
    }

    protected async Task<PulseTask> SeedTaskAsync(
        string title,
        Guid projectId,
        int points = 3,
        int dueDaysFromNow = 10,
        Guid? assigneeId = null,
        bool requiresQa = false,
        bool requiresPrApproval = false)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var task = PulseTask.Create(title, points, projectId,
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(dueDaysFromNow)));
        // This helper bypasses the real create command (and its TaskNumberAllocator), so it has to
        // assign one itself — a project's tasks otherwise collide on the (project_id, task_number)
        // unique index the moment a second task is seeded into the same project.
        var nextTaskNumber = (await db.Tasks.Where(t => t.ProjectId == projectId)
            .Select(t => (int?)t.TaskNumber).MaxAsync() ?? 0) + 1;
        task.AssignTaskNumber(nextTaskNumber);
        if (assigneeId.HasValue)
            task.Assign(assigneeId.Value, assigneeId.Value);
        if (requiresQa)
            task.SetRequiresQa(true);
        if (requiresPrApproval)
            task.SetRequiresPrApproval(true);
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    protected async Task AssignEngineerToTeamAsync(Guid engineerId, Guid teamId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var engineer = await db.Engineers.FirstAsync(e => e.Id == engineerId);
        engineer.AssignToTeam(teamId);
        await db.SaveChangesAsync();
    }

    protected async Task AssignTaskToSprintAsync(Guid taskId, Guid sprintId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var task = await db.Tasks.FirstAsync(t => t.Id == taskId);
        task.AssignToSprint(sprintId);
        await db.SaveChangesAsync();
    }

    protected async Task MarkTaskDoneAsync(Guid taskId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var task = await db.Tasks.FirstAsync(t => t.Id == taskId);
        task.MarkDone(Guid.NewGuid());
        await db.SaveChangesAsync();
    }

    protected async Task<TaskComment> SeedCommentAsync(Guid taskId, Guid authorId, string body = "a comment")
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var comment = TaskComment.Create(taskId, authorId, body);
        db.TaskComments.Add(comment);
        await db.SaveChangesAsync();
        return comment;
    }

    protected async Task<Sprint> SeedSprintAsync(Guid teamId, string name = "Sprint 1", Guid? projectId = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var sprint = Sprint.Create(teamId, projectId, name,
            DateOnly.FromDateTime(DateTime.UtcNow),
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)));
        db.Sprints.Add(sprint);
        await db.SaveChangesAsync();
        return sprint;
    }

    protected async Task SetResetTokenAsync(Guid engineerId, string rawToken, DateTime expiresAt)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtService>();
        var engineer = await db.Engineers.FirstAsync(e => e.Id == engineerId);
        var tokenHash = jwt.HashToken(rawToken);
        engineer.SetPasswordResetToken(tokenHash, expiresAt);
        await db.SaveChangesAsync();
    }
}
