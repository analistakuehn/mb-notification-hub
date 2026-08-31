using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// The two ingestion facts a mask that only reads the payload's top level gets
/// wrong: the value of a sensitive variable the producer sends one level down
/// lands in clear inside an append-only column, and a body that repeats a key
/// takes the whole request down.
/// </summary>
[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class NestedSensitiveVariableIngestionTests(NotificationsApiFixture fixture)
{
    private static readonly string[] RequiredOrderId = ["orderId"];

    private const string PlantedCpf = "99900011234";

    [RequiresDockerFact]
    public async Task The_stored_masked_projection_of_a_nested_sensitive_variable_never_carries_the_value()
    {
        var templateKey = await PublishTemplateWithNestedCpfAsync(["cpf"]);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-nested-mask", NotificationsApi.SendTransactional);
        var idempotencyKey = $"nested-mask-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(
                templateKey,
                recipientId: recipientId,
                variables: new
                {
                    orderId = "ord-1",
                    cliente = new { cpf = PlantedCpf, nome = "Ana" },
                }),
            idempotencyKey);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement body = await NotificationsApi.ReadJsonAsync(response);
        NotificationId.TryParse(body.GetProperty("notificationId").GetString()!, out Guid storedId)
            .ShouldBeTrue();

        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == storedId));

        notification.VariablesMaskedJson.Contains(PlantedCpf, StringComparison.Ordinal)
            .ShouldBeFalse("a projeção mascarada gravada carrega o CPF em claro.");

        using var masked = JsonDocument.Parse(notification.VariablesMaskedJson);
        masked.RootElement.GetProperty("cliente").GetProperty("cpf").GetString().ShouldBe("***");
        masked.RootElement.GetProperty("cliente").GetProperty("nome").GetString().ShouldBe("Ana");
        masked.RootElement.GetProperty("orderId").GetString().ShouldBe("ord-1");
    }

    [RequiresDockerFact]
    public async Task A_rest_payload_with_a_duplicated_key_is_accepted_instead_of_failing()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(
            fixture, sensitiveVariables: ["code"]);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-duplicated-key", NotificationsApi.SendTransactional);
        var idempotencyKey = $"duplicated-key-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";

        // The repeated key is not the sensitive one: materializing the payload
        // to find the sensitive one is what breaks, whatever key repeats.
        var rawBody = $$"""
            {
              "application": "{{NotificationsApi.Application}}",
              "recipientId": "{{recipientId}}",
              "class": "transactional",
              "templateKey": "{{templateKey}}",
              "locale": "pt-BR",
              "ttlSeconds": 300,
              "variables": { "orderId": "ord-1", "orderId": "ord-2" }
            }
            """;
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/notifications")
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response = await producer.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var stored = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(candidate => candidate.IdempotencyKey == idempotencyKey));
        stored.ShouldBe(1);
    }

    /// <summary>
    /// Publishes a template whose schema declares <c>cpf</c> both at the root
    /// and under <c>cliente</c>. The root declaration is what lets the same
    /// template publish before and after the mask learns to walk deeper, so the
    /// stored projection is the only thing the assertion moves.
    /// </summary>
    private async Task<string> PublishTemplateWithNestedCpfAsync(string[] sensitiveVariables)
    {
        HttpClient author = fixture.CreateAuthorClient("template-author-nested");
        HttpClient publisher = fixture.CreatePublisherClient("template-publisher-nested");
        var key = TemplateApi.NewKey("ntf-nested");

        HttpResponseMessage created = await author.PostAsJsonAsync("/v1/templates", new
        {
            key,
            application = NotificationsApi.Application,
            @class = "transactional",
            ownerTeam = "growth-squad",
            purpose = "order-updates",
            legalBasis = "execucao-de-contrato",
            defaultLocale = "pt-BR",
        });
        created.EnsureSuccessStatusCode();

        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} de {{ cliente.nome }}.</p>",
            bodyText = "Pedido {{ orderId }} de {{ cliente.nome }}.",
        }, etag);
        etag = await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new
            {
                orderId = new { type = "string" },
                cpf = new { type = "string" },
                cliente = new
                {
                    type = "object",
                    properties = new
                    {
                        cpf = new { type = "string" },
                        nome = new { type = "string" },
                    },
                },
            },
            required = RequiredOrderId,
        }, etag);
        await TemplateApi.PutSensitiveVariablesAsync(author, key, version, sensitiveVariables, etag);
        await TemplateApi.PublishAsync(publisher, key, version);
        return key;
    }
}
