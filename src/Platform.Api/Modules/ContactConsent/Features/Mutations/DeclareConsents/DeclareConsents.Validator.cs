using FluentValidation;
using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class DeclareConsents
{
    private const int MaxConsents = 50;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.Consents)
                .NotEmpty()
                .WithMessage("Consents must declare at least one (purpose, channel) state.");
            RuleFor(command => command.Consents)
                .Must(consents => consents is null || consents.Count <= MaxConsents)
                .WithMessage($"Consents accepts at most {MaxConsents} entries.")
                .Must(HaveNoDuplicatePairs)
                .WithMessage("Consents must not repeat the same (purpose, channel) pair.");
            RuleForEach(command => command.Consents).ChildRules(consent =>
            {
                consent.RuleFor(declaration => declaration.Purpose)
                    .NotEmpty()
                    .MaximumLength(100);
                consent.RuleFor(declaration => declaration.Channel)
                    .Must(ContactChannels.IsCanonical)
                    .WithMessage($"Channel must be one of: {string.Join(", ", ContactChannels.CanonicalValues)}.");
                consent.RuleFor(declaration => declaration.Source)
                    .Must(ConsentSources.IsCanonical)
                    .WithMessage($"Source must be one of: {string.Join(", ", ConsentSources.CanonicalValues)}.");
                consent.RuleFor(declaration => declaration.TermsVersion)
                    .NotEmpty()
                    .MaximumLength(50);
            });
        }

        private static bool HaveNoDuplicatePairs(IReadOnlyList<ConsentDeclaration>? consents)
        {
            if (consents is null)
            {
                return true;
            }

            var seen = new HashSet<(string, string)>();
            foreach (ConsentDeclaration consent in consents)
            {
                if (string.IsNullOrWhiteSpace(consent.Purpose) || string.IsNullOrWhiteSpace(consent.Channel))
                {
                    continue;
                }

                if (!seen.Add((consent.Purpose, consent.Channel)))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
