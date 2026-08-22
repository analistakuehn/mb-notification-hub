using FluentValidation;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutLayoutVersionContent
{
    internal sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(request => request.Body).NotEmpty().MaximumLength(LayoutVersion.MaxBodyLength);
            RuleFor(request => request.BodyText).MaximumLength(LayoutVersion.MaxBodyLength);
        }
    }
}
