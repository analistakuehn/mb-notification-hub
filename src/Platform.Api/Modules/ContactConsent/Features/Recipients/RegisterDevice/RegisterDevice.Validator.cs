using FluentValidation;
using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Recipients;

internal static partial class RegisterDevice
{
    private const int MaxTokenLength = 512;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.Token)
                .NotEmpty()
                .MaximumLength(MaxTokenLength);
            RuleFor(command => command.Platform)
                .Must(DevicePlatforms.IsCanonical)
                .WithMessage($"Platform must be one of: {string.Join(", ", DevicePlatforms.CanonicalValues)}.");
            RuleFor(command => command.AppVersion).MaximumLength(50);
        }
    }
}
