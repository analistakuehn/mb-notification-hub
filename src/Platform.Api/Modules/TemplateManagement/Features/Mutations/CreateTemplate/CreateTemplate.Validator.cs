using FluentValidation;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateTemplate
{
    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.Key).NotEmpty().MaximumLength(TemplateKey.MaxLength);
            RuleFor(command => command.Application).NotEmpty().MaximumLength(Template.MaxApplicationLength);
            RuleFor(command => command.Class)
                .Must(value => NotificationClasses.Create(value).IsSuccess)
                .WithMessage($"Class must be one of: {string.Join(", ", NotificationClasses.CanonicalValues)}.");
            RuleFor(command => command.OwnerTeam).NotEmpty().MaximumLength(Template.MaxTextLength);
            RuleFor(command => command.Purpose).NotEmpty().MaximumLength(Template.MaxTextLength);
            RuleFor(command => command.LegalBasis).NotEmpty().MaximumLength(Template.MaxTextLength);
        }
    }
}
