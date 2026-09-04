using System.Reflection;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class ChannelTests
{
    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    [InlineData("push")]
    [InlineData("whatsapp")]
    public void Accepts_every_supported_channel(string value)
    {
        Result<Channel> result = Channel.Create(value);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Value.ShouldBe(value);
    }

    [Fact]
    public void Matching_is_case_insensitive_and_yields_the_canonical_instance()
    {
        Result<Channel> result = Channel.Create("EMAIL");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(Channel.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fax")]
    [InlineData("e-mail")]
    public void Rejects_unknown_channels(string value)
    {
        Result<Channel> result = Channel.Create(value);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    /// <summary>
    /// What rehydration from stored data has to do, stated without saying
    /// anything about which object it hands back. This half survives any
    /// change to how a channel is compared.
    /// </summary>
    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    [InlineData("push")]
    [InlineData("whatsapp")]
    public void Rehydrates_a_persisted_channel_to_its_canonical_value(string value)
    {
        var rehydrated = Channel.Trusted(value);

        rehydrated.Value.ShouldBe(value);
        Channel.All.Select(channel => channel.Value).ShouldContain(value);
    }

    [Fact]
    public void Refuses_to_rehydrate_a_channel_outside_the_closed_set()
        => Should.Throw<InvalidOperationException>(() => Channel.Trusted("fax"));

    /// <summary>
    /// The other half, and a separate claim: a channel is identified by the
    /// instance, so every door hands back the one object the closed set holds
    /// and the type declares no comparison of its own. Rules elsewhere lean on
    /// this, because it is what makes reference equality over a channel answer
    /// the same question as comparing its value. Give the type value equality
    /// and this half is what should go red, while the behaviour above stands.
    /// </summary>
    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    [InlineData("push")]
    [InlineData("whatsapp")]
    public void Identifies_a_channel_by_the_instance_the_closed_set_holds(string value)
    {
        Channel canonical = Channel.All.Single(channel =>
            string.Equals(channel.Value, value, StringComparison.Ordinal));

        Channel.Trusted(value).ShouldBeSameAs(canonical);
        Channel.Create(value).Value.ShouldBeSameAs(canonical);

        var declaredComparison = typeof(Channel)
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Where(name => name is nameof(Equals) or nameof(GetHashCode) or "op_Equality" or "op_Inequality")
            .Order(StringComparer.Ordinal)
            .ToArray();

        declaredComparison.ShouldBeEmpty();
    }
}
