using System.Text.Json;
using FluentValidation;

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
                .WithMessage("Variables must be a JSON object.");
        }
    }
}
