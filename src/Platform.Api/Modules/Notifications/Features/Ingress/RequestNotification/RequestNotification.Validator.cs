using System.Text.Json;
using FluentValidation;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification;

internal static partial class RequestNotification
{
    /// <summary>
    /// Thirty days; a notification older than that is operationally
    /// meaningless. It is also the bound the attempt window is derived from,
    /// so the two are asserted against each other rather than kept in step by
    /// hand.
    /// </summary>
    internal const int MaxTtlSeconds = 2_592_000;

    internal sealed class Validator : AbstractValidator<Command>
    {
        /// <summary>
        /// Why a payload was refused, when the reason is not its size. The
        /// wording names the shape of the fault and nothing the producer sent,
        /// the same rule the ceiling's refusal follows: quoting the offending
        /// text back would put a value nothing could read into a response and
        /// into a dead-letter record.
        /// </summary>
        private const string UnreadableVariablesMessage =
            "Variables must be JSON text that can be read: an escape in it names no character.";

        private const string UnreadableMetadataMessage =
            "Metadata must be JSON text that can be read: an escape in it names no character.";

        public Validator()
        {
            RuleFor(command => command.Application).NotEmpty().MaximumLength(100);
            RuleFor(command => command.RecipientId).NotEmpty().MaximumLength(100);
            RuleFor(command => command.Class)
                .Must(NotificationClasses.IsCanonical)
                .WithMessage($"Class must be one of: {string.Join(", ", NotificationClasses.CanonicalValues)}.");
            RuleFor(command => command.TemplateKey).NotEmpty().MaximumLength(200);
            RuleFor(command => command.Locale).MaximumLength(20);
            RuleFor(command => command.TtlSeconds).GreaterThan(0).LessThanOrEqualTo(MaxTtlSeconds);
            RuleFor(command => command.CorrelationId).MaximumLength(200);
            RuleFor(command => command.Variables)
                .Must(BeAnObjectOrAbsent)
                .WithMessage("Variables must be a JSON object.");
            RuleFor(command => command.Metadata)
                .Must(BeAnObjectOrAbsent)
                .WithMessage("Metadata must be a JSON object.");

            // The catalog publishes the variables rule because it owns what
            // the payload costs: the allowlist scan walks every string value
            // of it at this gate and again at render, and the sandbox turns it
            // into script objects. This module owns the metadata rule because
            // it owns the cost that one bounds: the idempotency payload hash
            // canonicalizes metadata recursively, on every accepted request
            // and again on every replay resolved against a stored
            // registration. Shape validation is the only point ahead of all of
            // them, so both are imposed here or nowhere.
            //
            // Each field is decided by one call, because one traversal
            // discovers both of its refusals. Asking only about the size is
            // what left this door open: a payload the transcoding cannot read
            // throws inside the question, and on the bus that throw is what
            // stops a partition instead of dead-lettering one record.
            RuleFor(command => command.Variables)
                .Custom((variables, context) =>
                {
                    switch (VariablesPayloadLimit.Assess(variables))
                    {
                        case VariablesPayloadAdmission.Unreadable:
                            context.AddFailure(UnreadableVariablesMessage);
                            break;
                        case VariablesPayloadAdmission.AboveCeiling:
                            context.AddFailure(
                                "Variables must serialize to at most "
                                + $"{VariablesPayloadLimit.MaxBytes} bytes of JSON.");
                            break;
                        default:
                            break;
                    }
                });
            RuleFor(command => command.Metadata)
                .Custom((metadata, context) =>
                {
                    switch (MetadataPayloadSize.Assess(metadata))
                    {
                        case MetadataPayloadVerdict.Unreadable:
                            context.AddFailure(UnreadableMetadataMessage);
                            break;
                        case MetadataPayloadVerdict.AboveCeiling:
                            context.AddFailure(
                                "Metadata must serialize to at most "
                                + $"{MetadataPayloadSize.MaxBytes} bytes of JSON.");
                            break;
                        default:
                            break;
                    }
                });
            RuleFor(command => command.ChannelsHint)

                // A hint longer than the canonical channel set cannot say
                // anything a shorter one cannot: past that length it only
                // repeats a channel or names one that does not exist, and both
                // are already inert. What the field is promised to become is a
                // reordering within the channels the policy already allows,
                // never an addition, so the size of that set is the longest
                // the hint can ever need to be. The number is read from the
                // catalog rather than copied, so a fifth channel carries the
                // ceiling with it, and the idempotency payload hash stops
                // taking a list of unbounded length from the producer.
                .Must(hint => hint is null || hint.Count <= Channel.All.Count)
                .WithMessage($"ChannelsHint must name at most {Channel.All.Count} channels.");

            // Only worth itemizing a list that is not already refused for its
            // length. Each element carries its own error keyed by position, so
            // running these over a list of tens of thousands would answer an
            // oversized request with an oversized refusal.
            RuleForEach(command => command.ChannelsHint)
                .NotEmpty()
                .MaximumLength(20)
                .When(command => command.ChannelsHint is null
                    || command.ChannelsHint.Count <= Channel.All.Count);
        }

        private static bool BeAnObjectOrAbsent(JsonElement? value)
            => value is null or { ValueKind: JsonValueKind.Object or JsonValueKind.Null };
    }
}
