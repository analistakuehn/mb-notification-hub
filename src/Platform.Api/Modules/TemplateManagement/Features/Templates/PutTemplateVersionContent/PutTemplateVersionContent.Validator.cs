using FluentValidation;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class PutTemplateVersionContent
{
    internal sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(request => request.Body).NotEmpty().MaximumLength(TemplateSourceSize.MaxChars);
            RuleFor(request => request.Subject).MaximumLength(TemplateVersion.MaxSubjectLength);
            RuleFor(request => request.BodyText).MaximumLength(TemplateSourceSize.MaxChars);
        }
    }
}
