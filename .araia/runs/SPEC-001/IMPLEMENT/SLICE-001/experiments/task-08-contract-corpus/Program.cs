using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ContractCorpus;

internal static class Program
{
    private const string CurrentMinimalHash =
        "ae72ea096493d48cfcd34578542f2249b677872c5834778919df27af34cdbdeb";
    private const string CurrentCompleteHash =
        "135fb9992e7260f847834935d5dff24a98664975989a3dd57962082b11f6557c";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        WriteIndented = true,
    };

    public static int Main()
    {
        var minimal = new CandidateRequest(
            "araia-cambio",
            "cus_01J5X9",
            "critical",
            "auth.otp.login",
            300);
        CandidateRequest complete = minimal with
        {
            Locale = "pt-BR",
            Variables = Element("""{"expiresInMinutes":5,"code":"482913"}"""),
            ChannelsHint = ["email", "sms"],
            CorrelationId = "trace-7c1e",
            Metadata = Element("""{"origin":"producer","attempt":1}"""),
            ScheduledAt = new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.FromHours(-3)),
        };
        var absent = ComputeHash(minimal);
        var explicitNull = ComputeHash(minimal with { Attachments = null });
        var empty = ComputeHash(minimal with { Attachments = [] });
        var ordered = ComputeHash(minimal with { Attachments = ["att_alpha", "att_beta"] });
        var inverted = ComputeHash(minimal with { Attachments = ["att_beta", "att_alpha"] });
        CandidateRequest duplicate = minimal with { Attachments = ["att_alpha", "att_alpha"] };
        var changedReference = ComputeHash(
            minimal with { Attachments = ["att_alpha", "att_gamma"] });

        Require(absent == CurrentMinimalHash, "O hash mínimo vigente mudou.");
        Require(ComputeHash(complete) == CurrentCompleteHash, "O hash completo vigente mudou.");
        Require(absent == explicitNull && absent == empty, "Ausência, null e vazio divergiram.");
        Require(ordered != inverted, "A ordem deixou de ser significativa.");
        Require(!HasUniqueReferences(duplicate.Attachments), "A duplicata deveria ser recusada.");
        Require(ordered != changedReference, "A troca de referência não alterou o hash.");

        var oldProducerJson = JsonSerializer.Serialize(
            new OldRestRequest("app", "recipient", "transactional", "template", 300),
            WireOptions);
        NewRestRequest? oldProducerToNewServer = JsonSerializer.Deserialize<NewRestRequest>(
            oldProducerJson,
            WireOptions);
        const string newRestJson =
            """{"application":"app","recipientId":"recipient","class":"transactional","templateKey":"template","ttlSeconds":300,"attachments":["att_alpha"]}""";
        OldRestRequest? newPayloadToOldContract = JsonSerializer.Deserialize<OldRestRequest>(
            newRestJson,
            WireOptions);

        Require(oldProducerToNewServer?.Attachments is null, "O produtor REST antigo não vinculou no servidor novo.");
        Require(newPayloadToOldContract is not null, "O contrato REST antigo recusou o novo membro.");
        Require(
            !JsonSerializer.Serialize(newPayloadToOldContract, WireOptions)
                .Contains("attachments", StringComparison.Ordinal),
            "O contrato REST antigo não descartou silenciosamente o novo membro.");

        using var v1WithAttachment = JsonDocument.Parse(newRestJson);
        OldKafkaRequest? oldKafka = BindV1(v1WithAttachment.RootElement);
        Require(oldKafka is not null, "O binder Kafka V1 antigo recusou o membro desconhecido.");

        RouteResult v1Old = Route("araia.notification.requested.v1", Element(oldProducerJson));
        RouteResult v1WithNewMember = Route(
            "araia.notification.requested.v1",
            v1WithAttachment.RootElement);
        RouteResult v2New = Route("araia.notification.requested.v2", v1WithAttachment.RootElement);
        RouteResult unsupported = Route("araia.notification.requested.v3", v1WithAttachment.RootElement);

        Require(v1Old == RouteResult.V1Accepted, "O leitor novo deixou de aceitar V1 antigo.");
        Require(v1WithNewMember == RouteResult.V1MemberRejected, "V1 aceitou attachments.");
        Require(v2New == RouteResult.V2Accepted, "V2 não vinculou attachments.");
        Require(unsupported == RouteResult.TypeUnsupported, "O tipo desconhecido não foi recusado.");

        var released = new ReleasedAttachment(
            "att_alpha",
            "content_01",
            "documento.pdf",
            "application/pdf");
        Require(
            Fingerprint(released) != Fingerprint(released with { DisplayName = "outro.pdf" }),
            "O nome liberado não participa da identidade do snapshot.");
        Require(
            Fingerprint(released) != Fingerprint(released with { MediaType = "application/octet-stream" }),
            "O tipo liberado não participa da identidade do snapshot.");
        Require(
            Fingerprint(released) != Fingerprint(released with { ContentIdentity = "content_02" }),
            "A identidade liberada não participa da identidade do snapshot.");

        var report = new
        {
            hashes = new
            {
                absent,
                explicitNull,
                empty,
                currentComplete = ComputeHash(complete),
                ordered,
                inverted,
                changedReference,
            },
            duplicateRejected = !HasUniqueReferences(duplicate.Attachments),
            rest = new
            {
                oldProducerToNewServer = "accepted-without-attachments",
                newPayloadToOldContract = "accepted-attachments-silently-dropped",
            },
            kafka = new
            {
                oldV1BinderWithAttachment = "accepted-attachments-silently-dropped",
                newRouterV1 = v1Old.ToString(),
                newRouterV1WithAttachment = v1WithNewMember.ToString(),
                newRouterV2 = v2New.ToString(),
                newRouterUnknown = unsupported.ToString(),
            },
            snapshot = new
            {
                baseline = Fingerprint(released),
                changedName = Fingerprint(released with { DisplayName = "outro.pdf" }),
                changedType = Fingerprint(released with { MediaType = "application/octet-stream" }),
                changedContent = Fingerprint(released with { ContentIdentity = "content_02" }),
            },
        };
        Console.WriteLine(JsonSerializer.Serialize(report, ReportOptions));
        return 0;
    }

    private static JsonElement Element(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static bool HasUniqueReferences(IReadOnlyList<string>? references)
        => references is null || references.Count == references.Distinct(StringComparer.Ordinal).Count();

    private static string ComputeHash(CandidateRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("application", request.Application);
            if (request.Attachments is { Count: > 0 })
            {
                writer.WriteStartArray("attachments");
                foreach (var reference in request.Attachments)
                {
                    writer.WriteStringValue(reference);
                }

                writer.WriteEndArray();
            }

            if (request.ChannelsHint is not null)
            {
                writer.WriteStartArray("channelsHint");
                foreach (var channel in request.ChannelsHint)
                {
                    writer.WriteStringValue(channel);
                }

                writer.WriteEndArray();
            }

            writer.WriteString("class", request.Class);
            if (request.CorrelationId is not null)
            {
                writer.WriteString("correlationId", request.CorrelationId);
            }

            if (request.Metadata is { ValueKind: JsonValueKind.Object } metadata)
            {
                writer.WritePropertyName("metadata");
                WriteCanonical(metadata, writer);
            }

            writer.WriteString("recipientId", request.RecipientId);
            if (request.ScheduledAt is { } scheduledAt)
            {
                writer.WriteString(
                    "scheduledAt",
                    scheduledAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            }

            writer.WriteString("templateKey", request.TemplateKey);
            writer.WriteNumber("ttlSeconds", request.TtlSeconds);
            if (request.Variables is { ValueKind: JsonValueKind.Object } variables)
            {
                writer.WritePropertyName("variables");
                WriteCanonical(variables, writer);
            }

            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static OldKafkaRequest? BindV1(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new OldKafkaRequest(
            ReadString(data, "application"),
            ReadString(data, "recipientId"),
            ReadString(data, "class"),
            ReadString(data, "templateKey"),
            ReadInt32(data, "ttlSeconds"));
    }

    private static RouteResult Route(string type, JsonElement data)
    {
        if (string.Equals(type, "araia.notification.requested.v1", StringComparison.Ordinal))
        {
            return data.TryGetProperty("attachments", out _)
                ? RouteResult.V1MemberRejected
                : BindV1(data) is null
                    ? RouteResult.PayloadInvalid
                    : RouteResult.V1Accepted;
        }

        if (string.Equals(type, "araia.notification.requested.v2", StringComparison.Ordinal))
        {
            NewRestRequest? request = JsonSerializer.Deserialize<NewRestRequest>(
                data.GetRawText(),
                WireOptions);
            return request?.Attachments is { Count: > 0 }
                ? RouteResult.V2Accepted
                : RouteResult.PayloadInvalid;
        }

        return RouteResult.TypeUnsupported;
    }

    private static string? ReadString(JsonElement data, string name)
        => data.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt32(JsonElement data, string name)
        => data.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static string Fingerprint(ReleasedAttachment attachment)
    {
        var canonical = string.Join(
            "\n",
            attachment.Reference,
            attachment.ContentIdentity,
            attachment.DisplayName,
            attachment.MediaType);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed record CandidateRequest(
    string Application,
    string RecipientId,
    string Class,
    string TemplateKey,
    int TtlSeconds)
{
    public string? Locale { get; init; }

    public JsonElement? Variables { get; init; }

    public IReadOnlyList<string>? ChannelsHint { get; init; }

    public string? CorrelationId { get; init; }

    public JsonElement? Metadata { get; init; }

    public DateTimeOffset? ScheduledAt { get; init; }

    public IReadOnlyList<string>? Attachments { get; init; }
}

internal sealed record OldRestRequest(
    string Application,
    string RecipientId,
    string Class,
    string TemplateKey,
    int TtlSeconds);

internal sealed record NewRestRequest(
    string Application,
    string RecipientId,
    string Class,
    string TemplateKey,
    int TtlSeconds)
{
    public IReadOnlyList<string>? Attachments { get; init; }
}

internal sealed record OldKafkaRequest(
    string? Application,
    string? RecipientId,
    string? Class,
    string? TemplateKey,
    int TtlSeconds);

internal sealed record ReleasedAttachment(
    string Reference,
    string ContentIdentity,
    string DisplayName,
    string MediaType);

internal enum RouteResult
{
    V1Accepted,
    V1MemberRejected,
    V2Accepted,
    TypeUnsupported,
    PayloadInvalid,
}
