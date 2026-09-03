using FluentValidation;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RegisterAttachment
{
    internal sealed class Validator : AbstractValidator<Command>
    {
        /// <summary>
        /// The size rule reads the approved ceiling instead of a constant, so
        /// the answer a producer gets at registration and the capacity the
        /// module was configured with cannot drift apart.
        /// </summary>
        public Validator(IOptions<AttachmentCapacityOptions> capacity)
        {
            ArgumentNullException.ThrowIfNull(capacity);

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
                .LessThanOrEqualTo(capacity.Value.MaxAttachmentBytes);
        }
    }
}
