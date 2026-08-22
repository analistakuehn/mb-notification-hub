using FluentValidation;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class DisableLayout
{
    internal const int MaxReasonLength = 500;

    internal sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
            => RuleFor(request => request.Reason)
                .NotEmpty()
                .MaximumLength(MaxReasonLength);
    }
}
