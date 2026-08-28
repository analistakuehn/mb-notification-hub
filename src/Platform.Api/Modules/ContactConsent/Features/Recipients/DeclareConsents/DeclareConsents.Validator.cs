using FluentValidation;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Recipients;

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
                    .MaximumLength(ConsentPurpose.MaxLength);
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

                // The pair is compared on the canonical purpose, which is the
                // key the ledger resolves under: two spellings of one purpose
                // in one request are the same pair declared twice, and letting
                // them through would append two records that contradict each
                // other inside a single transaction.
                if (!seen.Add((ConsentPurpose.Canonicalize(consent.Purpose), consent.Channel)))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
