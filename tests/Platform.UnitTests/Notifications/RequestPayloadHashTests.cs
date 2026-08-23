using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Features.Mutations;

namespace NotificationHub.UnitTests.Notifications;

public sealed class RequestPayloadHashTests
{
    private static RequestNotification.Command BaseCommand(
        string variablesJson = """{"code":"482913","expiresInMinutes":5}""")
        => new(
            Application: "araia-cambio",
            RecipientId: "cus_01J5X9",
            Class: "critical",
            TemplateKey: "auth.otp.login",
            Locale: "pt-BR",
            TtlSeconds: 300)
        {
            Variables = JsonDocument.Parse(variablesJson).RootElement.Clone(),
            CorrelationId = "trace-7c1e",
        };

    [Fact]
    public void The_same_body_always_hashes_identically()
    {
        var first = RequestNotification.ComputePayloadHash(BaseCommand());
        var second = RequestNotification.ComputePayloadHash(BaseCommand());

        second.ShouldBe(first);
        first.Length.ShouldBe(64);
        first.ShouldBe(first.ToLowerInvariant());
    }

    [Fact]
    public void Variables_property_order_and_whitespace_never_change_the_hash()
    {
        var compact = RequestNotification.ComputePayloadHash(
            BaseCommand("""{"code":"482913","expiresInMinutes":5}"""));
        var reorderedAndSpaced = RequestNotification.ComputePayloadHash(
            BaseCommand("""{ "expiresInMinutes": 5,  "code": "482913" }"""));

        reorderedAndSpaced.ShouldBe(compact);
    }

    [Fact]
    public void A_different_variable_value_changes_the_hash()
    {
        var original = RequestNotification.ComputePayloadHash(
            BaseCommand("""{"code":"482913","expiresInMinutes":5}"""));
        var tampered = RequestNotification.ComputePayloadHash(
            BaseCommand("""{"code":"999999","expiresInMinutes":5}"""));

        tampered.ShouldNotBe(original);
    }

    [Fact]
    public void An_absent_optional_field_and_a_json_null_variables_hash_alike()
    {
        RequestNotification.Command withNullVariables = BaseCommand() with
        {
            Variables = JsonDocument.Parse("null").RootElement.Clone(),
        };
        RequestNotification.Command withoutVariables = BaseCommand() with { Variables = null };

        RequestNotification.ComputePayloadHash(withNullVariables)
            .ShouldBe(RequestNotification.ComputePayloadHash(withoutVariables));
    }

    [Fact]
    public void The_channels_hint_order_is_part_of_the_payload()
    {
        RequestNotification.Command pushFirst = BaseCommand() with { ChannelsHint = ["push", "sms"] };
        RequestNotification.Command smsFirst = BaseCommand() with { ChannelsHint = ["sms", "push"] };

        RequestNotification.ComputePayloadHash(pushFirst)
            .ShouldNotBe(RequestNotification.ComputePayloadHash(smsFirst));
    }

    [Fact]
    public void The_same_scheduled_instant_hashes_alike_across_time_zone_offsets()
    {
        RequestNotification.Command utc = BaseCommand() with
        {
            ScheduledAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
        };
        RequestNotification.Command saoPaulo = BaseCommand() with
        {
            ScheduledAt = new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.FromHours(-3)),
        };

        RequestNotification.ComputePayloadHash(saoPaulo)
            .ShouldBe(RequestNotification.ComputePayloadHash(utc));
    }

    [Fact]
    public void A_different_recipient_changes_the_hash()
    {
        RequestNotification.Command other = BaseCommand() with { RecipientId = "cus_9ZZZZZ" };

        RequestNotification.ComputePayloadHash(other)
            .ShouldNotBe(RequestNotification.ComputePayloadHash(BaseCommand()));
    }
}
