using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Compliance.Features.Disclosure;

internal static partial class GetAttemptContent
{
    /// <summary>
    /// The stored content of one attempt, opened for an auditor. Only the masked
    /// form is ever served: it is what the store keeps once the send reached a
    /// terminal verdict, and it is the form the recorded masked hash vouches
    /// for. The answer names the form it served, because a reader must never
    /// have to infer which of the two phases produced the text in front of them.
    /// </summary>
    /// <remarks>
    /// The complete form has no verification member and never will: once the
    /// masking replaced it, no stored bytes reproduce the hash it left behind.
    /// The hash still travels, declared, because it is the anchor for
    /// confronting evidence that came from outside the hub.
    /// </remarks>
    internal sealed record Response
    {
        public required string NotificationId { get; init; }

        public required int Sequence { get; init; }

        /// <summary>State of the attempt when the content was opened.</summary>
        public required string AttemptStatus { get; init; }

        public required string Channel { get; init; }

        public required string Locale { get; init; }

        public required string Body { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Subject { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BodyText { get; init; }

        /// <summary>Which of the two forms of the render was served.</summary>
        public required string DisclosedForm { get; init; }

        /// <summary>Hash recorded for the masked form when the attempt was queued.</summary>
        public required string ContentHashMasked { get; init; }

        /// <summary>The same hash recomputed over exactly the fields served above.</summary>
        public required string RecomputedContentHashMasked { get; init; }

        /// <summary>Whether the recomputed hash matches the recorded one.</summary>
        public required bool ContentHashMaskedVerified { get; init; }

        /// <summary>Hash recorded for the complete form, declared for external confrontation.</summary>
        public required string ContentHashFull { get; init; }

        /// <summary>
        /// True while the attempt has not reached a terminal verdict, so the
        /// store still holds the complete form beside the masked one. The served
        /// content is the masked form either way.
        /// </summary>
        public required bool CompleteFormStillStored { get; init; }
    }
}
