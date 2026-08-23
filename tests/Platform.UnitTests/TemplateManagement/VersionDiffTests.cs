using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class VersionDiffTests
{
    [Fact]
    public void Content_entries_only_in_the_base_version_are_reported_as_added()
    {
        ContentSetDiff diff = VersionDiff.DiffContents(
            [Entry("email", "pt-BR", body: "corpo"), Entry("sms", "pt-BR", body: "sms")],
            [Entry("email", "pt-BR", body: "corpo")]);

        diff.Added.ShouldBe([new ContentUnit("sms", "pt-BR")]);
        diff.Removed.ShouldBeEmpty();
        diff.Changed.ShouldBeEmpty();
    }

    [Fact]
    public void Content_entries_only_in_the_other_version_are_reported_as_removed()
    {
        ContentSetDiff diff = VersionDiff.DiffContents(
            [Entry("email", "pt-BR", body: "corpo")],
            [Entry("email", "pt-BR", body: "corpo"), Entry("push", "en", body: "push")]);

        diff.Added.ShouldBeEmpty();
        diff.Removed.ShouldBe([new ContentUnit("push", "en")]);
        diff.Changed.ShouldBeEmpty();
    }

    [Fact]
    public void An_entry_present_in_both_versions_reports_exactly_the_fields_that_differ()
    {
        ContentSetDiff diff = VersionDiff.DiffContents(
            [Entry("email", "pt-BR", subject: "Novo assunto", body: "corpo", bodyText: "texto")],
            [Entry("email", "pt-BR", subject: "Assunto", body: "corpo", bodyText: null)]);

        diff.Changed.Count.ShouldBe(1);
        diff.Changed[0].Channel.ShouldBe("email");
        diff.Changed[0].Locale.ShouldBe("pt-BR");
        diff.Changed[0].ChangedFields.ShouldBe(["bodyText", "subject"]);
    }

    [Fact]
    public void Identical_content_sets_produce_an_empty_diff()
    {
        ContentSetDiff diff = VersionDiff.DiffContents(
            [Entry("email", "pt-BR", subject: "Assunto", body: "corpo")],
            [Entry("email", "pt-BR", subject: "Assunto", body: "corpo")]);

        diff.Added.ShouldBeEmpty();
        diff.Removed.ShouldBeEmpty();
        diff.Changed.ShouldBeEmpty();
    }

    [Fact]
    public void Schema_fields_added_removed_and_changed_are_reported_by_name()
    {
        const string baseSchema = """
            {
              "type": "object",
              "properties": {
                "orderId": { "type": "string" },
                "amount": { "type": "number" },
                "portalUrl": { "type": "string", "format": "url" }
              },
              "required": ["orderId"]
            }
            """;
        const string againstSchema = """
            {
              "type": "object",
              "properties": {
                "orderId": { "type": "string" },
                "amount": { "type": "string" },
                "customerName": { "type": "string" }
              },
              "required": ["orderId"]
            }
            """;

        SchemaFieldDiff diff = VersionDiff.DiffVariablesSchemas(baseSchema, againstSchema);

        diff.AddedFields.ShouldBe(["portalUrl"]);
        diff.RemovedFields.ShouldBe(["customerName"]);
        diff.ChangedFields.ShouldBe(["amount"]);
    }

    [Fact]
    public void Flipping_a_field_between_required_and_optional_counts_as_a_change()
    {
        const string baseSchema = """{"properties":{"code":{"type":"string"}},"required":["code"]}""";
        const string againstSchema = """{"properties":{"code":{"type":"string"}}}""";

        SchemaFieldDiff diff = VersionDiff.DiffVariablesSchemas(baseSchema, againstSchema);

        diff.ChangedFields.ShouldBe(["code"]);
    }

    [Fact]
    public void Formatting_and_key_order_do_not_produce_schema_changes()
    {
        const string baseSchema = """{"properties":{"code":{"type":"string","maxLength":6}}}""";
        const string againstSchema = """{ "properties": { "code": { "maxLength": 6, "type": "string" } } }""";

        SchemaFieldDiff diff = VersionDiff.DiffVariablesSchemas(baseSchema, againstSchema);

        diff.AddedFields.ShouldBeEmpty();
        diff.RemovedFields.ShouldBeEmpty();
        diff.ChangedFields.ShouldBeEmpty();
    }

    [Fact]
    public void An_absent_schema_surfaces_the_counterpart_fields_as_added_or_removed()
    {
        const string schema = """{"properties":{"code":{"type":"string"}}}""";

        SchemaFieldDiff addedSide = VersionDiff.DiffVariablesSchemas(schema, null);
        SchemaFieldDiff removedSide = VersionDiff.DiffVariablesSchemas(null, schema);

        addedSide.AddedFields.ShouldBe(["code"]);
        removedSide.RemovedFields.ShouldBe(["code"]);
    }

    [Fact]
    public void An_unreadable_schema_contributes_no_fields_instead_of_failing()
    {
        SchemaFieldDiff diff = VersionDiff.DiffVariablesSchemas(
            "{ not json",
            """{"properties":{"code":{"type":"string"}}}""");

        diff.AddedFields.ShouldBeEmpty();
        diff.RemovedFields.ShouldBe(["code"]);
        diff.ChangedFields.ShouldBeEmpty();
    }

    [Fact]
    public void Object_field_diff_reports_added_removed_and_changed_top_level_fields()
    {
        const string baseDefinition = """
            {
              "schemaVersion": 1,
              "channelsAllowed": ["push", "sms"],
              "defaultTtl": "600s",
              "quietHours": { "from": "22:00", "to": "07:00" }
            }
            """;
        const string againstDefinition = """
            {
              "schemaVersion": 1,
              "channelsAllowed": ["push", "sms"],
              "defaultTtl": "300s",
              "consentPurpose": "marketing"
            }
            """;

        SchemaFieldDiff diff = VersionDiff.DiffObjectFields(baseDefinition, againstDefinition);

        diff.AddedFields.ShouldBe(["quietHours"]);
        diff.RemovedFields.ShouldBe(["consentPurpose"]);
        diff.ChangedFields.ShouldBe(["defaultTtl"]);
    }

    [Fact]
    public void Object_field_diff_ignores_formatting_and_key_order()
    {
        const string baseDefinition = """{"deliveryPlan":[{"channel":"push","timeout":"30s"}]}""";
        const string againstDefinition = """{ "deliveryPlan": [ { "timeout": "30s", "channel": "push" } ] }""";

        SchemaFieldDiff diff = VersionDiff.DiffObjectFields(baseDefinition, againstDefinition);

        diff.AddedFields.ShouldBeEmpty();
        diff.RemovedFields.ShouldBeEmpty();
        diff.ChangedFields.ShouldBeEmpty();
    }

    [Fact]
    public void An_explicitly_null_field_diffs_exactly_like_an_absent_one()
    {
        const string baseDefinition = """{"schemaVersion":1,"quietHours":null}""";
        const string againstDefinition = """{"schemaVersion":1}""";

        SchemaFieldDiff diff = VersionDiff.DiffObjectFields(baseDefinition, againstDefinition);

        diff.AddedFields.ShouldBeEmpty();
        diff.RemovedFields.ShouldBeEmpty();
        diff.ChangedFields.ShouldBeEmpty();
    }

    [Fact]
    public void An_unreadable_definition_contributes_no_fields_instead_of_failing()
    {
        SchemaFieldDiff diff = VersionDiff.DiffObjectFields(
            """{"schemaVersion":1}""",
            "{ not json");

        diff.AddedFields.ShouldBe(["schemaVersion"]);
        diff.RemovedFields.ShouldBeEmpty();
        diff.ChangedFields.ShouldBeEmpty();
    }

    private static ContentFieldSet Entry(
        string channel,
        string locale,
        string? subject = null,
        string? body = null,
        string? bodyText = null)
        => new(channel, locale, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["subject"] = subject,
            ["body"] = body,
            ["bodyText"] = bodyText,
        });
}
