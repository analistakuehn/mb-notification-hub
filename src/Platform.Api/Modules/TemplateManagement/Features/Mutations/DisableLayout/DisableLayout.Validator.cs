using FluentValidation;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class DisableLayout
{
    internal const int MaxNoteLength = 500;

    internal sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(request => request.Reason)
                .NotEmpty()
                .Must(LifecycleReasons.IsCanonical)
                .WithMessage($"Reason must be one of: {string.Join(", ", LifecycleReasons.CanonicalValues)}.");

            // The only refusal the note adds, and it is bounded on purpose.
            // Taking an artifact out of circulation is an emergency switch:
            // turning down a traffic stop because the operator found no entry
            // in the list is worse than the ambiguity the list removes. So
            // every other code goes through with no note at all, and the
            // escape hatch pays for itself by saying what happened.
            RuleFor(request => request.Note)
                .NotEmpty()
                .When(request => string.Equals(request.Reason, LifecycleReasons.Other, StringComparison.Ordinal))
                .WithMessage($"Note is required when Reason is '{LifecycleReasons.Other}'.");

            RuleFor(request => request.Note).MaximumLength(MaxNoteLength);
        }
    }
}
