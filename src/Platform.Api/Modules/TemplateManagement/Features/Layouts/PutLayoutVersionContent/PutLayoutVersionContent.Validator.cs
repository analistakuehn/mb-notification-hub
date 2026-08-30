using FluentValidation;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class PutLayoutVersionContent
{
    internal sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(request => request.Body).NotEmpty().MaximumLength(TemplateSourceSize.MaxChars);
            RuleFor(request => request.BodyText).MaximumLength(TemplateSourceSize.MaxChars);
        }
    }
}
