using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;

/// <summary>
/// Opaque keyset-pagination cursor: the base64url-encoded key of the last item
/// on the previous page.
/// </summary>
internal static class PageCursor
{
    internal static string Encode(string lastKey)
        => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(lastKey));

    internal static Result<string> Decode(string cursor)
    {
        try
        {
            return Result.Success(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor)));
        }
        catch (FormatException)
        {
            return Result.ValidationError<string>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                "The cursor is not valid. Use the nextCursor value returned by the previous page."));
        }
    }
}
