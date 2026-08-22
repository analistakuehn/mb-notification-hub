using FluentValidation;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class RollbackLayout
{
    internal sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
            => RuleFor(request => request.ToVersion).GreaterThanOrEqualTo(1);
    }
}
