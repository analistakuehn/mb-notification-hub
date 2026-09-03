using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Domain;

/// <summary>Opaque public reference that never carries storage coordinates.</summary>
public sealed record AttachmentReference
{
    public const string Prefix = "att_";
    public const int Length = 36;

    private AttachmentReference(string value) => Value = value;

    public string Value { get; }

    public static AttachmentReference Generate()
        => new(Format(Guid.CreateVersion7()));

    public static Result<AttachmentReference> Create(string? value)
        => IsValid(value)
            ? Result.Success(new AttachmentReference(value!))
            : Result.ValidationError<AttachmentReference>(ErrorCodes.InvalidReference);

    /// <summary>Rehydrates a reference that already passed validation.</summary>
    internal static AttachmentReference Trusted(string value) => new(value);

    public override string ToString() => Value;

    private static string Format(Guid value)
        => $"{Prefix}{value:N}";

    private static bool IsValid(string? value)
        => value is not null
            && value.Length == Length
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(value.AsSpan(Prefix.Length), "N", out _);
}
