using Pulse.Api.Controllers;
using Pulse.Domain.Engineers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class UpdateUserRequestValidator : AbstractValidator<UsersController.UpdateUserRequest>
{
    private static readonly string[] ValidRoles =
        [Roles.Engineer, Roles.Designer, Roles.TeamLead, Roles.ProductManager, Roles.ProjectManager,
         Roles.HeadOfRnD, Roles.HeadOfProduct, Roles.HeadOfDesign, Roles.HeadOfPmo, Roles.HeadOfFunctional, Roles.HeadOfCoreBanking, Roles.HeadOfInfraDevOps,
         Roles.Executive, Roles.HR];

    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.Role)
            .Must(r => r is null || ValidRoles.Contains(r))
            .WithMessage($"Role must be one of: {string.Join(", ", ValidRoles)}");
    }
}
