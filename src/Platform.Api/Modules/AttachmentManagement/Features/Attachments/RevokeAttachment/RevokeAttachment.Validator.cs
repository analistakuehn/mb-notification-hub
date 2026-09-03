using FluentValidation;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RevokeAttachment
{
    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.Reason)
                .NotEmpty()
                .MaximumLength(AttachmentRevocation.MaxReasonLength);
        }
    }
}
