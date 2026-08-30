using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Features.Ingress;

namespace NotificationHub.UnitTests.Notifications.Ingress;

/// <summary>
/// The binder is the first thing the bus transport does with a producer body,
/// ahead of the shared validator and ahead of every rule the validator owns.
/// Whatever it cannot answer here, it answers by throwing, and a throw on this
/// path is read as a transient failure and stops the partition.
/// </summary>
public sealed class IngressRequestBinderTests
{
    /// <summary>
    /// Raw text on purpose. Spelled as a C# escape the compiler would fold it
    /// into one code unit and the body under test would never carry the six
    /// characters that make it unreadable.
    /// </summary>
    private const string LoneSurrogateEscape = @"\ud800";

    /// <summary>
    /// Seven of them. A property lookup only unescapes a candidate key whose
    /// escaped length reaches the length of the name being sought, so a single
    /// escape breaks the lookup of a short name and leaves the long ones
    /// working. Seven is past every name this binder looks up, which is what
    /// makes the refusal independent of which name happens to be read first.
    /// </summary>
    private const string PoisonedKey =
        @"\ud800\ud800\ud800\ud800\ud800\ud800\ud800";

    [Fact]
    public void A_body_whose_top_level_key_names_no_character_is_refused_and_never_throws()
    {
        // The premise, asserted rather than assumed: the body is legal JSON
        // text and the reader accepts it. That is the whole shape of the
        // fault. A body that never parsed would let the test pass while
        // proving nothing.
        using var document = JsonDocument.Parse($$"""
            {
              "{{PoisonedKey}}": 1,
              "application": "app",
              "recipientId": "cus_1",
              "idempotencyKey": "key-1",
              "class": "transactional",
              "templateKey": "tpl",
              "ttlSeconds": 300
            }
            """);
        document.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);

        IngressRequest? request = Should.NotThrow(() => IngressRequestBinder.Bind(document.RootElement));

        request.ShouldBeNull();
    }

    [Theory]
    [InlineData("application")]
    [InlineData("recipientId")]
    [InlineData("idempotencyKey")]
    [InlineData("templateKey")]
    [InlineData("locale")]
    [InlineData("correlationId")]
    public void A_value_that_names_no_character_is_refused_wherever_the_binder_reads_one(string field)
    {
        // Not only the key. Every field the binder reads as text transcodes it,
        // so a guard that covered the lookup and not the read would leave the
        // same throw on the same path under a different body.
        using var document = JsonDocument.Parse($$"""
            {
              "application": "app",
              "recipientId": "cus_1",
              "idempotencyKey": "key-1",
              "class": "transactional",
              "templateKey": "tpl",
              "ttlSeconds": 300,
              "{{field}}": "{{LoneSurrogateEscape}}"
            }
            """);

        IngressRequest? request = Should.NotThrow(() => IngressRequestBinder.Bind(document.RootElement));

        request.ShouldBeNull();
    }

    [Fact]
    public void An_ordinary_body_still_binds_and_carries_its_fields_through()
    {
        // The falsifying half. Without it the two refusals above would also be
        // produced by a binder that refused everything, and the bus would
        // dead-letter every record instead of the unreadable ones.
        using var document = JsonDocument.Parse("""
            {
              "unknownFutureField": "ignored",
              "application": "app",
              "recipientId": "cus_1",
              "idempotencyKey": "key-1",
              "class": "transactional",
              "templateKey": "tpl",
              "locale": "pt-BR",
              "ttlSeconds": 300,
              "variables": { "orderId": "ord-1" }
            }
            """);

        IngressRequest? request = IngressRequestBinder.Bind(document.RootElement);

        request.ShouldNotBeNull();
        request.IdempotencyKey.ShouldBe("key-1");
        request.Command.Application.ShouldBe("app");
        request.Command.TemplateKey.ShouldBe("tpl");
        request.Command.Locale.ShouldBe("pt-BR");
        request.Command.Variables.ShouldNotBeNull();
    }

    [Fact]
    public void The_refusal_covers_the_whole_body_and_not_only_the_fields_this_binder_reads()
    {
        // The guard is deliberately wider than the throw. Which read reaches
        // an unreadable escape first depends on which names are looked up and
        // on how long they are, so a guard aimed at the reads this binder
        // happens to perform today would reopen the moment a field is added,
        // renamed or reordered. A body where anything is unreadable is
        // refused whole, including the payload fields this binder only clones
        // and hands on. The shared validator still refuses those on its own,
        // which is what closes the same fault on the HTTP door.
        using var document = JsonDocument.Parse($$"""
            {
              "application": "app",
              "recipientId": "cus_1",
              "idempotencyKey": "key-1",
              "class": "transactional",
              "templateKey": "tpl",
              "ttlSeconds": 300,
              "variables": { "orderId": "{{LoneSurrogateEscape}}" }
            }
            """);

        IngressRequest? request = Should.NotThrow(() => IngressRequestBinder.Bind(document.RootElement));

        request.ShouldBeNull();
    }
}
