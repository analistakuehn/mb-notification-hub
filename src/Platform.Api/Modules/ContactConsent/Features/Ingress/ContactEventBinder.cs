using System.Text.Json;
using NotificationHub.Api.Modules.ContactConsent.Features.Recipients;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Ingress;

/// <summary>
/// Binds the body of a declaration event to the very command the REST route
/// binds from its own body.
///
/// Permissive about values and strict about the declared collection. A missing
/// or mistyped field inside an entry becomes an empty value that the shared
/// validator refuses with a field-level report, which is what makes one
/// vocabulary of refusal answer both transports. A body without the declared
/// collection, on the other hand, binds to nothing at all: the collection is
/// the whole truth of a declarative write, so reading an absent one as an
/// empty declaration would remove every contact point of a recipient on behalf
/// of a producer that said nothing.
/// </summary>
internal static class ContactEventBinder
{
    private const string ContactPointsProperty = "contactPoints";
    private const string ConsentsProperty = "consents";
    private const string ChannelProperty = "channel";

    internal static DeclareContactPoints.Command? BindContactPoints(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty(ContactPointsProperty, out JsonElement points)
            || points.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<DeclareContactPoints.ContactPointDeclaration> declarations =
        [
            .. points.EnumerateArray().Select(point => new DeclareContactPoints.ContactPointDeclaration(
                ReadString(point, ChannelProperty) ?? string.Empty,
                ReadString(point, "value") ?? string.Empty,
                ReadBoolean(point, "verified") ?? false)),
        ];

        return new DeclareContactPoints.Command(declarations)
        {
            Timezone = ReadString(data, "timezone"),
            Locale = ReadString(data, "locale"),
        };
    }

    internal static DeclareConsents.Command? BindConsents(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty(ConsentsProperty, out JsonElement consents)
            || consents.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<DeclareConsents.ConsentDeclaration> declarations =
        [
            .. consents.EnumerateArray().Select(consent => new DeclareConsents.ConsentDeclaration(
                ReadString(consent, "purpose") ?? string.Empty,
                ReadString(consent, ChannelProperty) ?? string.Empty,
                ReadBoolean(consent, "granted") ?? false,
                ReadString(consent, "source") ?? string.Empty,
                ReadString(consent, "termsVersion") ?? string.Empty)),
        ];

        return new DeclareConsents.Command(declarations);
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static bool? ReadBoolean(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
}
