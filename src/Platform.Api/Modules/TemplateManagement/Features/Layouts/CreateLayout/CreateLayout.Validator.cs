using FluentValidation;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class CreateLayout
{
    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.Key).NotEmpty().MaximumLength(LayoutKey.MaxLength);
            RuleFor(command => command.OwnerTeam).NotEmpty().MaximumLength(Layout.MaxTextLength);
            RuleFor(command => command.DefaultLocale)
                .Must(value => value is null || Locale.Create(value).IsSuccess)
                .WithMessage("Default locale must be a language tag such as 'pt' or 'pt-BR'.");
        }
    }
}
