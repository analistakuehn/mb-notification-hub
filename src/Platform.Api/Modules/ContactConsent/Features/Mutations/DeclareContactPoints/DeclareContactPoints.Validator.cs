using FluentValidation;
using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class DeclareContactPoints
{
    private const int MaxContactPoints = 20;
    private const int MaxValueLength = 320;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.ContactPoints)
                .NotNull()
                .WithMessage("ContactPoints must be present; declare an empty list to remove every contact point.");
            RuleFor(command => command.ContactPoints)
                .Must(points => points is null || points.Count <= MaxContactPoints)
                .WithMessage($"ContactPoints accepts at most {MaxContactPoints} entries.")
                .Must(HaveNoDuplicateValues)
                .WithMessage("ContactPoints must not repeat the same (channel, value) pair.");
            RuleForEach(command => command.ContactPoints).ChildRules(point =>
            {
                point.RuleFor(declaration => declaration.Channel)
                    .Must(ContactChannels.IsCanonical)
                    .WithMessage($"Channel must be one of: {string.Join(", ", ContactChannels.CanonicalValues)}.");
                point.RuleFor(declaration => declaration.Value)
                    .NotEmpty()
                    .MaximumLength(MaxValueLength);
            });
            RuleFor(command => command.Timezone)
                .Must(BeAnIanaTimezoneOrAbsent)
                .WithMessage("Timezone must be a valid IANA timezone id (for example America/Sao_Paulo).");
            RuleFor(command => command.Locale).MaximumLength(20);
        }

        private static bool HaveNoDuplicateValues(IReadOnlyList<ContactPointDeclaration>? points)
        {
            if (points is null)
            {
                return true;
            }

            var seen = new HashSet<(string, string)>();
            foreach (ContactPointDeclaration point in points)
            {
                if (!ContactChannels.IsCanonical(point.Channel) || string.IsNullOrWhiteSpace(point.Value))
                {
                    continue;
                }

                if (!seen.Add((point.Channel, ContactValue.Normalize(point.Channel, point.Value))))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool BeAnIanaTimezoneOrAbsent(string? timezone)
        {
            if (timezone is null)
            {
                return true;
            }

            if (timezone.Length > 50 || (!timezone.Contains('/') && timezone != "UTC"))
            {
                return false;
            }

            return TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out _);
        }
    }
}
