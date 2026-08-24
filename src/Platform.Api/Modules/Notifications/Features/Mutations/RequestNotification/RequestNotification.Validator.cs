using System.Text.Json;
using FluentValidation;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Features.Mutations;

internal static partial class RequestNotification
{
    /// <summary>Thirty days; a notification older than that is operationally meaningless.</summary>
    private const int MaxTtlSeconds = 2_592_000;

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
                .WithMessage("Variables must be a JSON object.");
            RuleFor(command => command.Metadata)
                .Must(BeAnObjectOrAbsent)
                .WithMessage("Metadata must be a JSON object.");
            RuleForEach(command => command.ChannelsHint).NotEmpty().MaximumLength(20);
        }

        private static bool BeAnObjectOrAbsent(JsonElement? value)
            => value is null or { ValueKind: JsonValueKind.Object or JsonValueKind.Null };
    }
}
