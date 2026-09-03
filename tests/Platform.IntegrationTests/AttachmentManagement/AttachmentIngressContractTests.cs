using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NotificationHub.IntegrationTests.Ingress;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

public sealed partial class AttachmentIngressContractTests(TestApplicationFactory factory)
    : IClassFixture<TestApplicationFactory>
{
    // Refrozen deliberately. The document grew three addresses: two producer
    // transitions the lifecycle already had and nothing could reach, and the
    // authorized reading that the single public reason for content refusals
    // depends on. The semantic assertions beside this digest name every one of
    // them, so a change that arrives without them fails there first and this
    // constant never becomes the only thing standing between a published
    // contract and a body nobody meant to publish.
    private const string OpenApiDocumentSha256 =
        "8c47c7468acc222c9c6bf89c7d241e9804efc14e22b29d527dae70489057755a";

    private const string KafkaRecordSnapshot =
        """{"specversion":"1.0","id":"\u003Cevent-id\u003E","source":"urn:araia:integration-tests","type":"araia.notification.requested.v1","time":"\u003Cevent-time\u003E","subject":"cus-contract","datacontenttype":"application/json","data":{"application":"billing-app","recipientId":"cus-contract","idempotencyKey":"idem-contract","class":"transactional","templateKey":"payment-confirmed","locale":"pt-BR","variables":{"code":"123456"},"ttlSeconds":300}}""";

    private const string Issuer = "notification-hub-dev-only";
    private const string Audience = "notification-hub";
    private const string SigningKey =
        "ZGV2LW9ubHkgc2lnbmluZyBrZXkgLSBuZXZlciB1c2Ugb3V0c2lkZSBsb2NhbGhvc3Q=";

    [Fact]
    public async Task The_openapi_document_body_matches_the_frozen_contract()
    {
        var document = await ReadOpenApiDocumentAsync();

        AssertOpenApiContract(document);
    }

    [Fact]
    public async Task The_attachment_openapi_surface_matches_the_semantic_contract()
    {
        var document = await ReadOpenApiDocumentAsync();

        AssertAttachmentOpenApiSurface(document);
    }

    /// <summary>
    /// The body published for the notification ingestion is the ingestion
    /// contract, and the manifest of attachment references is a member of it.
    /// The members are read back from the served document instead of from the
    /// type, because the document names a schema after the type and two
    /// contracts that share a name share the entry: the route would then be
    /// published with another resource's body while the type stayed correct.
    /// </summary>
    [Fact]
    public async Task The_published_ingestion_body_names_the_manifest_beside_the_ingestion_members()
    {
        var document = await ReadOpenApiDocumentAsync();
        JsonObject root = JsonNode.Parse(document).ShouldNotBeNull().AsObject();

        var members = SchemaMembers(
            ResolveSchema(root, RequestBodySchema(root, "/v1/notifications", "post")));

        members.Contains("attachments", StringComparer.Ordinal).ShouldBeTrue(
            "O corpo publicado da ingestão deixou de nomear o manifesto de anexos, "
            + "portanto nenhum cliente gerado a partir do documento consegue pedi-los, "
            + "e um corpo que os nomeie mesmo assim é aceito sem que nada os envie.");
        members.ShouldBe(
        [
            "application",
            "attachments",
            "channelsHint",
            "class",
            "correlationId",
            "locale",
            "metadata",
            "recipientId",
            "scheduledAt",
            "templateKey",
            "ttlSeconds",
            "variables",
        ]);
    }

    /// <summary>
    /// The ingestion and the attachment registration are published as two
    /// schemas. Both declare the same short type name, so a document that names
    /// its entries after that name alone keeps one of the two and points every
    /// operation at it, which publishes one of the routes with the body of the
    /// other and leaves a frozen digest unable to see either one change.
    /// </summary>
    [Fact]
    public async Task The_ingestion_body_and_the_attachment_registration_body_are_separate_published_schemas()
    {
        var document = await ReadOpenApiDocumentAsync();
        JsonObject root = JsonNode.Parse(document).ShouldNotBeNull().AsObject();

        var ingestion = SchemaReference(RequestBodySchema(root, "/v1/notifications", "post"));
        var registration = SchemaReference(RequestBodySchema(root, "/v1/attachments", "post"));

        ingestion.ShouldNotBe(
            registration,
            "As duas rotas apontam para o mesmo schema, portanto uma delas está "
            + "publicada com o corpo da outra.");
        SchemaMembers(ResolveSchema(root, RequestBodySchema(root, "/v1/attachments", "post")))
            .ShouldBe(["application", "contentType", "fileName", "sizeBytes"]);
    }

    [Fact]
    public async Task A_semantic_change_to_the_openapi_document_is_rejected_by_the_frozen_contract()
    {
        var document = await ReadOpenApiDocumentAsync();
        AssertOpenApiContract(document);
        JsonObject parsedDocument = JsonNode.Parse(document).ShouldNotBeNull().AsObject();
        JsonNode version = parsedDocument["openapi"].ShouldNotBeNull();
        var currentVersion = version.GetValue<string>();
        currentVersion.ShouldNotBeNullOrWhiteSpace();
        var frozenVersionMember = $"\"openapi\": \"{currentVersion}\"";
        document.Split(frozenVersionMember, StringSplitOptions.None).Length.ShouldBe(2);

        var changedDocument = document.Replace(
            frozenVersionMember,
            $"\"openapi\": \"{currentVersion}-changed\"",
            StringComparison.Ordinal);
        JsonNode.Parse(changedDocument).ShouldNotBeNull()["openapi"]
            .ShouldNotBeNull()
            .GetValue<string>()
            .ShouldBe($"{currentVersion}-changed");

        Should.Throw<ShouldAssertException>(
            () => AssertOpenApiContract(changedDocument));
    }

    [Fact]
    public void The_kafka_ingress_record_matches_the_frozen_serialized_contract()
    {
        var record = KafkaIngressApi.RequestedEvent(
            "billing-app",
            "payment-confirmed",
            "transactional",
            "cus-contract",
            "idem-contract");

        AssertKafkaContract(record);
    }

    [Fact]
    public void A_semantic_change_to_the_kafka_record_is_rejected_by_the_frozen_contract()
    {
        var record = KafkaIngressApi.RequestedEvent(
            "billing-app",
            "payment-confirmed",
            "transactional",
            "cus-contract",
            "idem-contract");
        JsonObject changedRecord = JsonNode.Parse(record).ShouldNotBeNull().AsObject();
        JsonObject data = changedRecord["data"].ShouldNotBeNull().AsObject();
        data["ttlSeconds"].ShouldNotBeNull().GetValue<int>().ShouldBe(300);

        data["ttlSeconds"] = 301;

        Should.Throw<ShouldAssertException>(
            () => AssertKafkaContract(changedRecord.ToJsonString()));
    }

    [Fact]
    public void An_event_id_with_a_trailing_line_feed_is_rejected_before_normalization()
    {
        var record = KafkaIngressApi.RequestedEvent(
            "billing-app",
            "payment-confirmed",
            "transactional",
            "cus-contract",
            "idem-contract");
        JsonObject changedRecord = JsonNode.Parse(record).ShouldNotBeNull().AsObject();
        changedRecord["id"] = $"evt-{new string('a', 32)}\n";

        Should.Throw<ShouldAssertException>(
            () => NormalizeVolatileKafkaMembers(changedRecord.ToJsonString()));
    }

    private static string NormalizeVolatileKafkaMembers(string json)
    {
        JsonObject record = JsonNode.Parse(json).ShouldNotBeNull().AsObject();
        JsonNode id = record["id"].ShouldNotBeNull();
        JsonNode time = record["time"].ShouldNotBeNull();
        id.GetValueKind().ShouldBe(JsonValueKind.String);
        time.GetValueKind().ShouldBe(JsonValueKind.String);
        var idValue = id.GetValue<string>();
        var timeValue = time.GetValue<string>();
        KafkaEventIdPattern().IsMatch(idValue).ShouldBeTrue();
        DateTimeOffset.TryParseExact(
            timeValue,
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTimeOffset eventTime).ShouldBeTrue();
        eventTime.Offset.ShouldBe(TimeSpan.Zero);

        record["id"] = "<event-id>";
        record["time"] = "<event-time>";
        return record.ToJsonString();
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void AssertKafkaContract(string record)
        => NormalizeVolatileKafkaMembers(record).ShouldBe(KafkaRecordSnapshot);

    private static void AssertOpenApiContract(string document)
    {
        AssertAttachmentOpenApiSurface(document);
        Sha256(document).ShouldBe(OpenApiDocumentSha256);
    }

    private static void AssertAttachmentOpenApiSurface(string document)
    {
        JsonObject root = JsonNode.Parse(document).ShouldNotBeNull().AsObject();
        JsonObject paths = root["paths"].ShouldNotBeNull().AsObject();
        var attachmentPaths = paths
            .Select(path => path.Key)
            .Where(path => path.StartsWith("/v1/attachments", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Two addresses were added on purpose, and both are transitions the
        // lifecycle already had and nothing could reach: asking for a verdict,
        // and taking a release back. They are published under the producer's
        // tree because the producer's grant is what gates them.
        attachmentPaths.ShouldBe(
        [
            "/v1/attachments",
            "/v1/attachments/{reference}",
            "/v1/attachments/{reference}/content",
            "/v1/attachments/{reference}/revocation",
            "/v1/attachments/{reference}/validation",
        ]);

        // The authorized reading sits outside that tree, deliberately. It
        // answers a different role, and hanging it under an address a producer
        // already holds a grant over would put a reading only operations may
        // perform inside the producer's own subtree.
        paths
            .Select(path => path.Key)
            .Where(path => path.StartsWith("/v1/attachment-operations", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(["/v1/attachment-operations/{reference}"]);

        JsonObject register = AssertOperation(
            paths,
            "/v1/attachments",
            "post",
            "201",
            "400",
            "401",
            "403",
            "503");
        JsonObject get = AssertOperation(
            paths,
            "/v1/attachments/{reference}",
            "get",
            "200",
            "401",
            "404",
            "503");
        JsonObject upload = AssertOperation(
            paths,
            "/v1/attachments/{reference}/content",
            "put",
            "200",
            "400",
            "401",
            "404",
            "409",
            "503");
        JsonObject validation = AssertOperation(
            paths,
            "/v1/attachments/{reference}/validation",
            "post",
            "200",
            "401",
            "404",
            "409",
            "503");
        JsonObject revocation = AssertOperation(
            paths,
            "/v1/attachments/{reference}/revocation",
            "post",
            "200",
            "400",
            "401",
            "404",
            "409",
            "503");
        JsonObject lifecycle = AssertOperation(
            paths,
            "/v1/attachment-operations/{reference}",
            "get",
            "200",
            "401",
            "403",
            "404");

        AssertRequestSchema(
            root,
            register,
            "application/json",
            "application",
            "contentType",
            "fileName",
            "sizeBytes");
        AssertBinaryRequestSchema(root, upload);
        AssertRequestSchema(root, revocation, "application/json", "reason");
        AssertPublicResponseSchema(root, register, "201");
        AssertPublicResponseSchema(root, get, "200");
        AssertPublicResponseSchema(root, upload, "200");

        // The two transitions answer with the same public shape the rest of the
        // producer surface answers with. A member added to either one would be
        // a word this module publishes to a producer, and the whole point of
        // the single reason for refusals is that there is no such word.
        AssertPublicResponseSchema(root, validation, "200");
        AssertPublicResponseSchema(root, revocation, "200");

        // The authorized reading answers with more, and this names exactly how
        // much more. Every member is an instant, a state or a declared reason;
        // the assertion below is what keeps a coordinate of the bytes from
        // arriving here later under a member nobody looked at.
        AssertResponseSchema(
            root,
            lifecycle,
            "200",
            "inconclusiveUntil",
            "reference",
            "releaseExpiresAt",
            "releasedAt",
            "revocationReason",
            "revokedAt",
            "state",
            "validationDetail");

        var responseContracts = new JsonArray(
            register["responses"]!.DeepClone(),
            get["responses"]!.DeepClone(),
            upload["responses"]!.DeepClone()).ToJsonString();
        foreach (var privateMember in new[]
                 {
                     "bucket",
                     "contentId",
                     "credential",
                     "digest",
                     "objectKey",
                     "objectLocator",
                     "serviceUrl",
                     "versionId",
                 })
        {
            responseContracts.ShouldNotContain(privateMember, Case.Insensitive);
        }
    }

    private static JsonObject AssertOperation(
        JsonObject paths,
        string path,
        string method,
        params string[] responseCodes)
    {
        JsonObject pathItem = paths[path].ShouldNotBeNull().AsObject();
        var methods = pathItem
            .Select(member => member.Key)
            .Where(member => member is "delete" or "get" or "head" or "options" or "patch" or "post" or "put" or "trace")
            .ToArray();
        methods.ShouldBe([method]);

        JsonObject operation = pathItem[method].ShouldNotBeNull().AsObject();
        var actualResponseCodes = operation["responses"]
            .ShouldNotBeNull()
            .AsObject()
            .Select(response => response.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        actualResponseCodes.ShouldBe(responseCodes.Order(StringComparer.Ordinal).ToArray());
        return operation;
    }

    private static void AssertRequestSchema(
        JsonObject root,
        JsonObject operation,
        string contentType,
        params string[] expectedProperties)
    {
        JsonObject schema = ResolveSchema(
            root,
            operation["requestBody"]
                .ShouldNotBeNull()
                .AsObject()["content"]
                .ShouldNotBeNull()
                .AsObject()[contentType]
                .ShouldNotBeNull()
                .AsObject()["schema"]
                .ShouldNotBeNull()
                .AsObject());
        var properties = schema["properties"]
            .ShouldNotBeNull()
            .AsObject()
            .Select(property => property.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        properties.ShouldBe(expectedProperties.Order(StringComparer.Ordinal).ToArray());
    }

    private static void AssertBinaryRequestSchema(JsonObject root, JsonObject operation)
    {
        JsonObject schema = ResolveSchema(
            root,
            operation["requestBody"]
                .ShouldNotBeNull()
                .AsObject()["content"]
                .ShouldNotBeNull()
                .AsObject()["application/octet-stream"]
                .ShouldNotBeNull()
                .AsObject()["schema"]
                .ShouldNotBeNull()
                .AsObject());
        schema["type"].ShouldNotBeNull().GetValue<string>().ShouldBe("string");
        schema["format"].ShouldNotBeNull().GetValue<string>().ShouldBe("binary");
    }

    private static void AssertPublicResponseSchema(
        JsonObject root,
        JsonObject operation,
        string responseCode)
        => AssertResponseSchema(root, operation, responseCode, "reference", "state");

    private static void AssertResponseSchema(
        JsonObject root,
        JsonObject operation,
        string responseCode,
        params string[] expectedProperties)
    {
        JsonObject schema = ResolveSchema(
            root,
            operation["responses"]
                .ShouldNotBeNull()
                .AsObject()[responseCode]
                .ShouldNotBeNull()
                .AsObject()["content"]
                .ShouldNotBeNull()
                .AsObject()["application/json"]
                .ShouldNotBeNull()
                .AsObject()["schema"]
                .ShouldNotBeNull()
                .AsObject());
        var properties = schema["properties"]
            .ShouldNotBeNull()
            .AsObject()
            .Select(property => property.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        properties.ShouldBe(expectedProperties.Order(StringComparer.Ordinal).ToArray());
    }

    private static JsonObject RequestBodySchema(JsonObject root, string path, string method)
        => root["paths"]
            .ShouldNotBeNull()
            .AsObject()[path]
            .ShouldNotBeNull()
            .AsObject()[method]
            .ShouldNotBeNull()
            .AsObject()["requestBody"]
            .ShouldNotBeNull()
            .AsObject()["content"]
            .ShouldNotBeNull()
            .AsObject()["application/json"]
            .ShouldNotBeNull()
            .AsObject()["schema"]
            .ShouldNotBeNull()
            .AsObject();

    private static string SchemaReference(JsonObject schema)
        => schema["$ref"]
            .ShouldNotBeNull(
                "O corpo deixou de referenciar um schema nomeado, então esta regra não "
                + "compara mais duas entradas do dicionário de schemas.")
            .GetValue<string>();

    private static string[] SchemaMembers(JsonObject schema)
        => schema["properties"]
            .ShouldNotBeNull()
            .AsObject()
            .Select(property => property.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static JsonObject ResolveSchema(JsonObject root, JsonObject schema)
    {
        JsonNode? reference = schema["$ref"];
        if (reference is null)
        {
            return schema;
        }

        var referenceValue = reference.GetValue<string>();
        var componentName = referenceValue[(referenceValue.LastIndexOf('/') + 1)..];
        return root["components"]
            .ShouldNotBeNull()
            .AsObject()["schemas"]
            .ShouldNotBeNull()
            .AsObject()[componentName]
            .ShouldNotBeNull()
            .AsObject();
    }

    private async Task<string> ReadOpenApiDocumentAsync()
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            IssueToken());

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        var document = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return document;
    }

    [GeneratedRegex(@"^evt-[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KafkaEventIdPattern();

    private static string IssueToken()
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = "openapi-contract-reader" },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(SigningKey)),
                SecurityAlgorithms.HmacSha256),
        });
}
