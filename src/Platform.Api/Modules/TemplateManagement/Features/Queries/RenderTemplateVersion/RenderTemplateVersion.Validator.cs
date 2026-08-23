using System.Text;
using System.Text.Json;
using FluentValidation;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class RenderTemplateVersion
{
    internal sealed class Validator : AbstractValidator<Request>
    {
        /// <summary>
        /// Ceiling for the serialized variables payload: preview rendering
        /// never needs more, and anything larger is rejected before the
        /// sandbox converts it into script objects.
        /// </summary>
        internal const int MaxVariablesBytes = 262_144;

        public Validator()
        {
            RuleFor(request => request.Channel).NotEmpty().MaximumLength(20);
            RuleFor(request => request.Locale).NotEmpty().MaximumLength(5);
            RuleFor(request => request.Variables)
                .Must(variables => variables is null
                    || variables.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined)
                .WithMessage("Variables must be a JSON object.")
                .Must(variables => variables is not { ValueKind: JsonValueKind.Object } provided
                    || Encoding.UTF8.GetByteCount(provided.GetRawText()) <= MaxVariablesBytes)
                .WithMessage($"Variables must serialize to at most {MaxVariablesBytes} bytes of JSON.");
        }
    }
}
