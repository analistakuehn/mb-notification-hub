using System.Text.Json;
using FluentValidation;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class RenderTemplateVersion
{
    internal sealed class Validator : AbstractValidator<Request>
    {
        /// <summary>
        /// Why the payload was refused, when the reason is not its size. The
        /// wording names the shape of the fault and nothing the caller sent,
        /// the same rule the ceiling's refusal follows: quoting the offending
        /// text back would put a value nothing could read into a response.
        /// </summary>
        private const string UnreadableMessage =
            "Variables must be JSON text that can be read: an escape in it names no character.";

        public Validator()
        {
            RuleFor(request => request.Channel).NotEmpty().MaximumLength(20);
            RuleFor(request => request.Locale).NotEmpty().MaximumLength(5);
            RuleFor(request => request.Variables)
                .Must(variables => variables is null
                    || variables.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined)
                .WithMessage("Variables must be a JSON object.");

            // The ceiling is the module's, not this endpoint's: preview never
            // needed more, and holding a private copy of the number here is
            // how the preview and the render that ships a message ended up
            // disagreeing about the same payload.
            //
            // One call decides both refusals, because one traversal discovers
            // both. Asking only about the size is what left this door open: a
            // payload the transcoding cannot read throws inside the question,
            // and the answer never comes back at all. The two are worded apart
            // because a payload that names no character is not an oversized
            // one, and answering it with the ceiling's message would name a
            // cause the caller could act on and be wrong about it.
            RuleFor(request => request.Variables)
                .Custom((variables, context) =>
                {
                    switch (VariablesPayloadSize.Assess(variables))
                    {
                        case VariablesPayloadVerdict.Unreadable:
                            context.AddFailure(UnreadableMessage);
                            break;
                        case VariablesPayloadVerdict.AboveCeiling:
                            context.AddFailure(
                                "Variables must serialize to at most "
                                + $"{VariablesPayloadSize.MaxBytes} bytes of JSON.");
                            break;
                        default:
                            break;
                    }
                });
        }
    }
}
