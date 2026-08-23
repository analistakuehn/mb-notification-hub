using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

/// <summary>Position of the last row of a page: the keyset the next page resumes from.</summary>
internal readonly record struct NotificationQueryPosition(DateTimeOffset CreatedAt, Guid Id);

/// <summary>
/// Opaque cursor of the notification history: base64url over the creation
/// instant in ISO 8601 UTC with microsecond precision plus the public
/// <c>ntf_</c> identity. The stored instant already comes back from PostgreSQL
/// truncated to the microsecond, so the round trip is exact and the keyset
/// comparison lands on the same row every time; the public identity keeps the
/// internal UUID out of the value a caller holds.
/// </summary>
/// <remarks>
/// Deliberately duplicated instead of shared with the catalog cursor of the
/// template surface: that one encodes a single string key and is internal to
/// its module, and promoting either into the shared kernel would turn a detail
/// of two routes into a platform contract.
/// </remarks>
internal static class NotificationQueryCursor
{
    private const char Separator = '|';
    private const string InstantFormat = "yyyy-MM-ddTHH:mm:ss.ffffffZ";

    internal static string Encode(NotificationQueryPosition position)
    {
        var value = string.Concat(
            position.CreatedAt.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture),
            Separator,
            NotificationId.Format(position.Id));
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(value));
    }

    internal static bool TryDecode(string cursor, out NotificationQueryPosition position)
    {
        position = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(Separator, StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                decoded[..separator],
                InstantFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime createdAt))
        {
            return false;
        }

        if (!NotificationId.TryParse(decoded[(separator + 1)..], out Guid id))
        {
            return false;
        }

        position = new NotificationQueryPosition(new DateTimeOffset(createdAt, TimeSpan.Zero), id);
        return true;
    }
}
