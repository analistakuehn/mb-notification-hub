using FluentValidation;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RegisterAttachment
{
    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.Application)
                .NotEmpty()
                .MaximumLength(Attachment.MaxApplicationLength);
            RuleFor(command => command.FileName)
                .NotEmpty()
                .MaximumLength(Attachment.MaxFileNameLength);
            RuleFor(command => command.ContentType)
                .NotEmpty()
                .MaximumLength(Attachment.MaxContentTypeLength)
                .Must(Attachment.IsValidMediaType)
                .WithMessage("Content type must be a syntactically valid media type.");
            RuleFor(command => command.SizeBytes)
                .GreaterThan(0)
                .LessThanOrEqualTo(Attachment.MaxSizeBytes);
        }
    }
}
