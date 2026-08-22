using FluentValidation;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutTemplateVersionContent
{
    internal sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(request => request.Body).NotEmpty().MaximumLength(TemplateVersion.MaxBodyLength);
            RuleFor(request => request.Subject).MaximumLength(TemplateVersion.MaxSubjectLength);
            RuleFor(request => request.BodyText).MaximumLength(TemplateVersion.MaxBodyLength);
        }
    }
}
