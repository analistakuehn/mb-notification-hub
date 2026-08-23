using System.Text.Json;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;

namespace NotificationHub.UnitTests.ContactConsent;

/// <summary>
/// The dead-letter body of this ingestion is rebuilt from an allow-list, not
/// copied and cleaned. These tests hold the line that nothing outside the list
/// can travel, whatever the refused record carries.
/// </summary>
public sealed class ContactIngestionRedactionTests
{
    private const string Email = "cliente@example.com";
    private const string Phone = "+5511999990000";

    private static readonly JsonElement ContactPointsData = Parse($$"""
        {
          "timezone": "America/Sao_Paulo",
          "contactPoints": [
            { "channel": "email", "value": "{{Email}}", "verified": true },
            { "channel": "sms", "value": "{{Phone}}", "verified": false }
          ]
        }
        """);

    private static readonly JsonElement ConsentsData = Parse("""
        {
          "consents": [
            {
              "purpose": "marketing",
              "channel": "email",
              "granted": true,
              "source": "app",
              "termsVersion": "v3"
            }
          ]
        }
        """);

    [Fact]
    public void The_summary_carries_the_channels_by_position_and_no_contact_value()
    {
        var summary = ContactIngestionDeadLetterWriter.Summarize(Diagnosis(ContactPointsData));

        summary.ShouldNotContain(Email);
        summary.ShouldNotContain(Phone);

        // Falsification: the original body does carry both, so the assertions
        // above measure the reconstruction and not the absence of a string.
        ContactPointsData.GetRawText().ShouldContain(Email);
        ContactPointsData.GetRawText().ShouldContain(Phone);

        JsonElement root = JsonDocument.Parse(summary).RootElement;
        root.GetProperty("contactPointCount").GetInt32().ShouldBe(2);
        root.GetProperty("contactPointChannels")
            .EnumerateArray()
            .Select(channel => channel.GetString())
            .ShouldBe(["email", "sms"]);
    }

    [Fact]
    public void The_summary_carries_the_envelope_facts_the_producer_diagnoses_by()
    {
        var summary = ContactIngestionDeadLetterWriter.Summarize(Diagnosis(ContactPointsData));

        JsonElement root = JsonDocument.Parse(summary).RootElement;
        root.GetProperty("reason").GetString()
            .ShouldBe(ContactIngestionRejectionReasons.PayloadInvalid);
        root.GetProperty("eventType").GetString()
            .ShouldBe("araia.contact.contact_points_declared.v1");
        root.GetProperty("eventSource").GetString().ShouldBe("urn:araia:cadastro");
        root.GetProperty("eventId").GetString().ShouldBe("evt-1");
    }

    [Fact]
    public void The_summary_carries_every_declared_consent_entry()
    {
        var summary = ContactIngestionDeadLetterWriter.Summarize(Diagnosis(ConsentsData));

        JsonElement entry = JsonDocument.Parse(summary).RootElement.GetProperty("consents")[0];
        entry.GetProperty("purpose").GetString().ShouldBe("marketing");
        entry.GetProperty("channel").GetString().ShouldBe("email");
        entry.GetProperty("granted").GetBoolean().ShouldBeTrue();
        entry.GetProperty("source").GetString().ShouldBe("app");
        entry.GetProperty("termsVersion").GetString().ShouldBe("v3");
    }

    [Fact]
    public void A_field_outside_the_allow_list_never_travels()
    {
        JsonElement data = Parse($$"""
            {
              "contactPoints": [
                { "channel": "email", "value": "{{Email}}", "valueHash": "0a1b2c", "cpf": "12345678900" }
              ]
            }
            """);

        var summary = ContactIngestionDeadLetterWriter.Summarize(Diagnosis(data));

        summary.ShouldNotContain("valueHash");
        summary.ShouldNotContain("0a1b2c");
        summary.ShouldNotContain("cpf");
        summary.ShouldNotContain("12345678900");
    }

    [Fact]
    public void A_body_that_is_not_an_object_loses_everything_but_the_envelope_facts()
    {
        var summary = ContactIngestionDeadLetterWriter.Summarize(
            Diagnosis(Parse($"\"{Email}\"")));

        summary.ShouldNotContain(Email);
        JsonElement root = JsonDocument.Parse(summary).RootElement;
        root.TryGetProperty("contactPointChannels", out _).ShouldBeFalse();
        root.TryGetProperty("consents", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_refusal_without_a_readable_envelope_still_names_its_reason()
    {
        var summary = ContactIngestionDeadLetterWriter.Summarize(new ContactIngestionDiagnosis
        {
            Reason = ContactIngestionRejectionReasons.PayloadInvalid,
        });

        JsonElement root = JsonDocument.Parse(summary).RootElement;
        root.GetProperty("reason").GetString()
            .ShouldBe(ContactIngestionRejectionReasons.PayloadInvalid);
        root.GetProperty("eventType").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static ContactIngestionDiagnosis Diagnosis(JsonElement data) => new()
    {
        Reason = ContactIngestionRejectionReasons.PayloadInvalid,
        EventType = "araia.contact.contact_points_declared.v1",
        EventSource = "urn:araia:cadastro",
        EventId = "evt-1",
        Data = data,
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
