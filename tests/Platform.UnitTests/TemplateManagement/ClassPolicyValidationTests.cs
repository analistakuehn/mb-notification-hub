using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class ClassPolicyValidationTests
{
    private const string CompleteDefinition = """
        {
          "schemaVersion": 1,
          "channelsAllowed": ["push", "sms", "whatsapp"],
          "deliveryPlan": [
            { "channel": "push", "timeout": "30s" },
            { "channel": "sms" }
          ],
          "defaultTtl": "300s",
          "dedupeWindow": "60s",
          "quietHours": null,
          "consentPurpose": null
        }
        """;

    [Fact]
    public void A_complete_version_1_definition_passes_every_check()
    {
        ValidationReport report = ClassPolicyValidation.Validate(CompleteDefinition);

        report.Passed.ShouldBeTrue();
        report.Checks.ShouldAllBe(check => check.Status == "passed");
    }

    [Fact]
    public void The_reader_materializes_the_six_typed_fields()
    {
        Result<ClassPolicyDefinition> read = ClassPolicyDefinition.Read(CompleteDefinition);

        read.IsSuccess.ShouldBeTrue();
        ClassPolicyDefinition definition = read.Value!;
        definition.SchemaVersion.ShouldBe(1);
        definition.ChannelsAllowed.ShouldBe([Channel.Push, Channel.Sms, Channel.WhatsApp]);
        definition.DeliveryPlan.Count.ShouldBe(2);
        definition.DeliveryPlan[0].ShouldBe(new DeliveryPlanStep(Channel.Push, TimeSpan.FromSeconds(30)));
        definition.DeliveryPlan[1].ShouldBe(new DeliveryPlanStep(Channel.Sms, null));
        definition.DefaultTtl.ShouldBe(TimeSpan.FromSeconds(300));
        definition.DedupeWindow.ShouldBe(TimeSpan.FromSeconds(60));
        definition.QuietHours.ShouldBeNull();
        definition.ConsentPurpose.ShouldBeNull();
    }

    [Fact]
    public void An_unknown_top_level_field_never_fails_the_read()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "channelsAllowed": ["push"],
              "deliveryPlan": [{ "channel": "push" }],
              "defaultTtl": "300s",
              "dedupeWindow": "60s",
              "futureVocabularyField": { "anything": true }
            }
            """;

        ClassPolicyValidation.Validate(definition).Passed.ShouldBeTrue();
        ClassPolicyDefinition.Read(definition).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_unknown_property_inside_a_delivery_step_is_tolerated()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "channelsAllowed": ["push"],
              "deliveryPlan": [{ "channel": "push", "when": "future-condition" }],
              "defaultTtl": "300s",
              "dedupeWindow": "60s"
            }
            """;

        ClassPolicyValidation.Validate(definition).Passed.ShouldBeTrue();
    }

    [Fact]
    public void Absent_quiet_hours_and_consent_purpose_read_as_null()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "channelsAllowed": ["email"],
              "deliveryPlan": [{ "channel": "email" }],
              "defaultTtl": "3600s",
              "dedupeWindow": "0s"
            }
            """;

        Result<ClassPolicyDefinition> read = ClassPolicyDefinition.Read(definition);

        read.IsSuccess.ShouldBeTrue();
        read.Value!.QuietHours.ShouldBeNull();
        read.Value!.ConsentPurpose.ShouldBeNull();
    }

    [Fact]
    public void Quiet_hours_and_consent_purpose_materialize_when_present()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "channelsAllowed": ["push"],
              "deliveryPlan": [{ "channel": "push" }],
              "defaultTtl": "600s",
              "dedupeWindow": "60s",
              "quietHours": { "from": "22:00", "to": "07:00" },
              "consentPurpose": "marketing"
            }
            """;

        Result<ClassPolicyDefinition> read = ClassPolicyDefinition.Read(definition);

        read.IsSuccess.ShouldBeTrue();
        read.Value!.QuietHours.ShouldBe(new QuietHoursWindow(new TimeOnly(22, 0), new TimeOnly(7, 0)));
        read.Value!.ConsentPurpose.ShouldBe("marketing");
    }

    [Fact]
    public void A_missing_required_field_fails_its_own_check()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "channelsAllowed": ["push"],
              "deliveryPlan": [{ "channel": "push" }],
              "dedupeWindow": "60s"
            }
            """;

        ValidationReport report = ClassPolicyValidation.Validate(definition);

        report.Passed.ShouldBeFalse();
        report.Checks.ShouldContain(check =>
            check.Name == "default-ttl" && check.Status == "failed" && check.Location == "defaultTtl");
    }

    [Fact]
    public void A_field_with_the_wrong_type_fails_its_own_check()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "channelsAllowed": "push",
              "deliveryPlan": [{ "channel": "push" }],
              "defaultTtl": "300s",
              "dedupeWindow": "60s"
            }
            """;

        ValidationReport report = ClassPolicyValidation.Validate(definition);

        report.Passed.ShouldBeFalse();
        report.Checks.ShouldContain(check =>
            check.Name == "channels-allowed" && check.Status == "failed");
    }

    [Fact]
    public void A_duration_out_of_range_fails_its_own_check()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "channelsAllowed": ["push"],
              "deliveryPlan": [{ "channel": "push" }],
              "defaultTtl": "0s",
              "dedupeWindow": "60s"
            }
            """;

        ValidationReport report = ClassPolicyValidation.Validate(definition);

        report.Passed.ShouldBeFalse();
        report.Checks.ShouldContain(check =>
            check.Name == "default-ttl" && check.Status == "failed");
    }

    [Fact]
    public void A_schema_version_above_the_vocabulary_fails()
    {
        const string definition = """
            {
              "schemaVersion": 2,
              "channelsAllowed": ["push"],
              "deliveryPlan": [{ "channel": "push" }],
              "defaultTtl": "300s",
              "dedupeWindow": "60s"
            }
            """;

        ValidationReport report = ClassPolicyValidation.Validate(definition);

        report.Passed.ShouldBeFalse();
        report.Checks.ShouldContain(check =>
            check.Name == "schema-version" && check.Status == "failed");
    }

    [Fact]
    public void A_delivery_step_outside_the_allowed_channels_fails()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "channelsAllowed": ["push"],
              "deliveryPlan": [{ "channel": "sms" }],
              "defaultTtl": "300s",
              "dedupeWindow": "60s"
            }
            """;

        ValidationReport report = ClassPolicyValidation.Validate(definition);

        report.Passed.ShouldBeFalse();
        report.Checks.ShouldContain(check =>
            check.Name == "delivery-plan"
            && check.Status == "failed"
            && check.Location == "deliveryPlan[0].channel");
    }

    [Fact]
    public void A_malformed_quiet_hours_window_fails()
    {
        const string definition = """
            {
              "schemaVersion": 1,
              "channelsAllowed": ["push"],
              "deliveryPlan": [{ "channel": "push" }],
              "defaultTtl": "300s",
              "dedupeWindow": "60s",
              "quietHours": { "from": "22h", "to": "07:00" }
            }
            """;

        ValidationReport report = ClassPolicyValidation.Validate(definition);

        report.Passed.ShouldBeFalse();
        report.Checks.ShouldContain(check =>
            check.Name == "quiet-hours" && check.Status == "failed");
    }

    [Fact]
    public void A_document_that_is_not_json_fails_the_document_check_without_throwing()
    {
        ValidationReport report = ClassPolicyValidation.Validate("not-json{");

        report.Passed.ShouldBeFalse();
        report.Checks.ShouldContain(check =>
            check.Name == "definition-document" && check.Status == "failed");
    }

    [Fact]
    public void Reading_an_invalid_definition_surfaces_the_first_failed_check()
    {
        Result<ClassPolicyDefinition> read = ClassPolicyDefinition.Read("[]");

        read.IsFailure.ShouldBeTrue();
        read.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        read.Error.ShouldNotBeNull();
        read.Error.ShouldContain("definition-document");
    }
}
