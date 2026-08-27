using System.Text.Json;
using FluentValidation;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class RenderTemplateVersion
{
    internal sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(request => request.Channel).NotEmpty().MaximumLength(20);
            RuleFor(request => request.Locale).NotEmpty().MaximumLength(5);
            RuleFor(request => request.Variables)
                .Must(variables => variables is null
                    || variables.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined)
                .WithMessage("Variables must be a JSON object.")

                // The ceiling is the module's, not this endpoint's: preview
                // never needed more, and holding a private copy of the number
                // here is how the preview and the render that ships a message
                // ended up disagreeing about the same payload.
                .Must(variables => !VariablesPayloadSize.ExceedsMaxBytes(variables))
                .WithMessage(
                    $"Variables must serialize to at most {VariablesPayloadSize.MaxBytes} bytes of JSON.");
        }
    }
}
