using FluentValidation;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Recipients;

internal static partial class RemoveSuppression
{
    private const int MaxJustificationLength = 500;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
            => RuleFor(command => command.Justification)
                .NotEmpty()
                .MaximumLength(MaxJustificationLength);
    }
}
