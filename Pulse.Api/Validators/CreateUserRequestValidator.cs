using Pulse.Api.Controllers;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentValidation;

namespace Pulse.Api.Validators;

public class CreateUserRequestValidator : AbstractValidator<UsersController.CreateUserRequest>
{
    private static readonly string[] ValidRoles =
    [
        Roles.Engineer, Roles.Designer, Roles.TeamLead, Roles.ProductManager, Roles.ProjectManager,
        Roles.HeadOfRnD, Roles.HeadOfProduct, Roles.HeadOfDesign, Roles.HeadOfPmo, Roles.HeadOfFunctional, Roles.HeadOfCoreBanking, Roles.HeadOfInfraDevOps,
        Roles.Executive, Roles.HR, Roles.Accountant,
    ];

    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Role).NotEmpty().Must(r => ValidRoles.Contains(r))
            .WithMessage($"Role must be one of: {string.Join(", ", ValidRoles)}");
        RuleFor(x => x.BaselinePoints).GreaterThan(0);
        RuleFor(x => x.BaselineCycleDays).GreaterThan(0);
        // Executive, HR, and Accountant are the roles explicitly designed to have no team — org-wide,
        // read-only stakeholders rather than members of any team. Every other role still requires one.
        RuleFor(x => x.TeamId).NotNull().WithMessage("Team is required.")
            .When(x => x.Role is not (Roles.Executive or Roles.HR or Roles.Accountant));
        RuleFor(x => x.Discipline)
            .Must(d => d is not null && Enum.TryParse<Discipline>(d, ignoreCase: true, out _))
            .When(x => x.IsQa)
            .WithMessage("A discipline is required for a QA engineer — it's how QA reviews get routed to them.");
    }
}
