using FluentValidation;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutTemplateVersionLayout
{
    internal sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(request => request.LayoutKey).MaximumLength(LayoutKey.MaxLength);
            RuleFor(request => request.LayoutVersion).GreaterThanOrEqualTo(1);
            RuleFor(request => request)
                .Must(request => request.LayoutKey is null == request.LayoutVersion is null)
                .WithMessage("A layout reference requires both layoutKey and layoutVersion, or neither.");
        }
    }
}
