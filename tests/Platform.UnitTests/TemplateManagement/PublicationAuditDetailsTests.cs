using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The audit details document of a publication. The report that reaches this
/// producer has already passed, so what is at stake is which part of it the
/// trail keeps and which part it must never carry.
/// </summary>
public sealed class PublicationAuditDetailsTests
{
    private const string ContentHash = "b8f1c2d3e4a5968778695a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d";

    /// <summary>
    /// A name that could only come from the message of a check, so a document
    /// carrying it is carrying content the message interpolated.
    /// </summary>
    private const string LeakProbe = "zxqvortex";

    [Fact]
    public void Every_check_name_that_ran_appears_in_the_details_document()
    {
        var report = new ValidationReport(
        [
            Passed(ValidationCheckNames.Compilation),
            Warned(ValidationCheckNames.VariablesUsed, LeakProbe),
            Passed(ValidationCheckNames.ChannelLimits),
        ]);

        JsonElement validation = ValidationOf(PublicationAuditDetails.ForPublication(ContentHash, null, report));

        Names(validation, "checks").ShouldBe(
        [
            ValidationCheckNames.ChannelLimits,
            ValidationCheckNames.Compilation,
            ValidationCheckNames.VariablesUsed,
        ]);
    }

    [Fact]
    public void A_check_that_only_warned_appears_in_the_warned_list()
    {
        var report = new ValidationReport(
        [
            Passed(ValidationCheckNames.Compilation),
            Warned(ValidationCheckNames.VariablesUsed, LeakProbe),
        ]);

        JsonElement validation = ValidationOf(PublicationAuditDetails.ForPublication(ContentHash, null, report));

        Names(validation, "warned").ShouldBe([ValidationCheckNames.VariablesUsed]);
        validation.GetProperty("warnings").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void A_check_that_passed_is_absent_from_the_warned_list()
    {
        var report = new ValidationReport(
        [
            Passed(ValidationCheckNames.Compilation),
            Warned(ValidationCheckNames.VariablesUsed, LeakProbe),
        ]);

        JsonElement validation = ValidationOf(PublicationAuditDetails.ForPublication(ContentHash, null, report));

        Names(validation, "warned").ShouldNotContain(ValidationCheckNames.Compilation);
    }

    [Fact]
    public void The_details_document_carries_no_validation_check_message()
    {
        var report = new ValidationReport(
        [
            Passed(ValidationCheckNames.Compilation),
            Warned(ValidationCheckNames.VariablesUsed, LeakProbe),
        ]);

        var details = PublicationAuditDetails.ForPublication(ContentHash, null, report);

        details.Contains(LeakProbe, StringComparison.Ordinal).ShouldBeFalse(
            "the message of a check interpolates content and must not reach the trail");
        details.Contains("email/pt-BR/body", StringComparison.Ordinal).ShouldBeFalse(
            "the location of a check points at the content unit that produced the finding");
    }

    [Fact]
    public void The_details_document_of_a_publication_stays_bounded_when_a_hundred_variables_warn()
    {
        List<ValidationCheck> checks = [Passed(ValidationCheckNames.Compilation)];
        for (var index = 0; index < 100; index++)
        {
            checks.Add(Warned(ValidationCheckNames.VariablesUsed, $"{LeakProbe}{index}"));
        }

        var details = PublicationAuditDetails.ForPublication(ContentHash, null, new ValidationReport(checks));

        Encoding.UTF8.GetByteCount(details).ShouldBeLessThan(1_024);
        details.Contains(LeakProbe, StringComparison.Ordinal).ShouldBeFalse(
            "a hundred warnings name a hundred variables and none of them belongs in the trail");
        ValidationOf(details).GetProperty("warnings").GetInt32().ShouldBe(100);
    }

    [Fact]
    public void The_details_document_of_a_publication_without_warnings_carries_an_empty_warned_list()
    {
        var report = new ValidationReport(
        [
            Passed(ValidationCheckNames.Compilation),
            Passed(ValidationCheckNames.VariablesUsed),
        ]);

        JsonElement validation = ValidationOf(PublicationAuditDetails.ForPublication(ContentHash, null, report));

        Names(validation, "warned").ShouldBeEmpty();
        validation.GetProperty("warnings").GetInt32().ShouldBe(0);
        Names(validation, "checks").ShouldContain(ValidationCheckNames.VariablesUsed);
    }

    private static ValidationCheck Passed(string name)
        => new(name, ValidationCheckStatuses.Passed, "Every rule of this check holds.", null);

    private static ValidationCheck Warned(string name, string variable)
        => new(
            name,
            ValidationCheckStatuses.Warning,
            $"Variable '{variable}' is declared but never used.",
            "email/pt-BR/body");

    private static JsonElement ValidationOf(string details)
    {
        using var document = JsonDocument.Parse(details);
        return document.RootElement.GetProperty("validation").Clone();
    }

    private static List<string> Names(JsonElement validation, string property)
        => [.. validation.GetProperty(property).EnumerateArray().Select(entry => entry.GetString()!)];
}
