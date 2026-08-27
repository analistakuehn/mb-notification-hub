using System.Text.Json;
using FluentValidation;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Mutations;

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
                .WithMessage("Variables must be a JSON object.")

                // The catalog publishes the ceiling because it owns what the
                // payload costs: the allowlist scan walks every string value
                // of it at this gate and again at render, and the sandbox
                // turns it into script objects. Shape validation is the only
                // point that can refuse it before both, so the ingestion reads
                // the number instead of choosing one.
                .Must(variables => !VariablesPayloadLimit.Exceeds(variables))
                .WithMessage(
                    $"Variables must serialize to at most {VariablesPayloadLimit.MaxBytes} bytes of JSON.");
            RuleFor(command => command.Metadata)
                .Must(BeAnObjectOrAbsent)
                .WithMessage("Metadata must be a JSON object.")

                // This module owns this ceiling because it owns the cost the
                // ceiling bounds: the idempotency payload hash canonicalizes
                // metadata recursively, on every accepted request and again on
                // every replay resolved against a stored registration. Nothing
                // downstream reads the field, so the catalog's number would be
                // the wrong one to borrow.
                .Must(metadata => !MetadataPayloadSize.ExceedsMaxBytes(metadata))
                .WithMessage(
                    $"Metadata must serialize to at most {MetadataPayloadSize.MaxBytes} bytes of JSON.");
            RuleForEach(command => command.ChannelsHint).NotEmpty().MaximumLength(20);
        }

        private static bool BeAnObjectOrAbsent(JsonElement? value)
            => value is null or { ValueKind: JsonValueKind.Object or JsonValueKind.Null };
    }
}
