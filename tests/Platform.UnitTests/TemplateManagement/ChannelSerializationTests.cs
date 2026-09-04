using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The canonical vocabulary on the wire. Before it could round trip, the
/// closed set wrote itself as a wrapper object and threw on the way back, so
/// every consumer projected the word by hand on both sides.
/// </summary>
public sealed class ChannelSerializationTests
{
    private static readonly JsonSerializerOptions Options =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    [InlineData("push")]
    [InlineData("whatsapp")]
    public void A_channel_writes_the_canonical_word_and_reads_back_the_instance(string word)
    {
        var canonical = Channel.Trusted(word);

        var document = JsonSerializer.Serialize(canonical, Options);

        document.ShouldBe($"\"{word}\"");
        JsonSerializer.Deserialize<Channel>(document, Options).ShouldBeSameAs(
            canonical,
            "um canal é identificado pela instância do conjunto fechado, então ler um de volta "
            + "tem de devolver essa instância e nunca um segundo objeto com o mesmo valor.");
    }

    [Theory]
    [InlineData("\"telegram\"")]
    [InlineData("{\"value\":\"email\"}")]
    [InlineData("7")]
    public void A_document_outside_the_vocabulary_is_refused_instead_of_admitted(string document)
        => Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Channel>(document, Options));

    /// <summary>
    /// The whole published definition survives a write, a read and a second
    /// write byte for byte. Structural equality is not the oracle here on
    /// purpose: the definition is a reference type without value equality by
    /// decision, because its identity is the published content hash and two
    /// definitions that project identically can still carry different
    /// documents.
    /// </summary>
    [Fact]
    public void A_published_definition_survives_a_round_trip_byte_for_byte()
    {
        var first = JsonSerializer.SerializeToUtf8Bytes(Definition(), Options);

        ClassPolicyDefinition read = JsonSerializer.Deserialize<ClassPolicyDefinition>(first, Options)!;
        var second = JsonSerializer.SerializeToUtf8Bytes(read, Options);

        second.ShouldBe(first);
        read.DeliveryPlan.Select(step => step.Channel).ShouldBe([Channel.Sms, Channel.Email]);
        read.ChannelsAllowed.ShouldBe([Channel.Sms, Channel.Email]);
    }

    /// <summary>
    /// The byte for byte oracle above is necessary and not sufficient, and this
    /// is the arm that says why. A round trip compares the serializer against
    /// itself, so it stays green over a document the module's own canonical
    /// parser refuses. The two forms of a duration already coexist in the
    /// codebase and this is not a regression of the converter: the stored
    /// policy document spells a duration as a whole number of seconds, while
    /// the serializer writes the framework form for a time span.
    /// <para>
    /// This path therefore never authors a policy document, and the assertion
    /// pins that: authoring goes through the operator's document and the
    /// canonical parser, which reports per field checks a serializer cannot
    /// produce. Should the two forms ever be reconciled, this test goes red and
    /// names the decision instead of letting the round trip hide it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_serialized_definition_is_not_a_policy_document_the_canonical_parser_accepts()
    {
        var document = Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(Definition(), Options));

        ValidationReport report = ClassPolicyValidation.Validate(document);

        ValidationCheck[] failed = [.. report.Checks
            .Where(check => check.Status == ValidationCheckStatuses.Failed)];
        failed.ShouldNotBeEmpty(
            "o parser canônico do módulo recusa a forma de duração que o serializador escreve, "
            + "então este caminho não é fonte de documento de política e o oráculo de ida e volta "
            + "sozinho ficaria verde sobre um documento que a autoria recusaria.");
        failed.Select(check => check.Name).ShouldContain(ClassPolicyCheckNames.DefaultTtl);
        failed.Single(check => check.Name == ClassPolicyCheckNames.DefaultTtl)
            .Message.ShouldContain(ClassPolicyValidation.DurationFormat);
    }

    private static ClassPolicyDefinition Definition()
        => new()
        {
            SchemaVersion = 1,
            ChannelsAllowed = [Channel.Sms, Channel.Email],
            DeliveryPlan =
            [
                new DeliveryPlanStep(Channel.Sms, TimeSpan.FromMinutes(10)),
                new DeliveryPlanStep(Channel.Email, null),
            ],
            DefaultTtl = TimeSpan.FromHours(1),
            DedupeWindow = TimeSpan.FromMinutes(5),
            QuietHours = new QuietHoursWindow(new TimeOnly(22, 0), new TimeOnly(7, 0)),
            ConsentPurpose = null,
        };
}
