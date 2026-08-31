using FluentValidation;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class PutTemplateVersionSensitiveVariables
{
    internal sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleForEach(request => request.SensitiveVariables)
                .NotEmpty()
                .MaximumLength(TemplateVersion.MaxVariableNameLength);
        }
    }
}
