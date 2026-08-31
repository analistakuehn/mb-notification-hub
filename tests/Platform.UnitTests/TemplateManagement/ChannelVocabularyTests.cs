using System.Reflection;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Completeness of the canonical vocabulary, and its relation to the channels
/// a contact point can address. Neither was asserted anywhere: adding a
/// channel to the closed set produced no error, no warning and no red, and a
/// serializer over the vocabulary says nothing about whether the vocabulary is
/// complete. These two guards are what makes a new channel arrive as a
/// decision instead of as silence.
/// </summary>
public sealed class ChannelVocabularyTests
{
    /// <summary>
    /// Every channel the type declares belongs to the published list. The
    /// declared members are read by reflection rather than listed here,
    /// because a list written by hand is exactly the thing that goes stale
    /// next to the member someone forgot to add.
    /// </summary>
    [Fact]
    public void The_published_list_holds_every_channel_the_closed_set_declares()
    {
        Channel[] declared = [.. typeof(Channel)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.FieldType == typeof(Channel))
            .Select(field => (Channel)field.GetValue(null)!)];

        declared.ShouldNotBeEmpty(
            "a leitura por reflexão é o oráculo deste portão; se ela devolver vazio o portão "
            + "fica verde por não medir nada.");
        Channel.All.ShouldBe(declared, ignoreOrder: true);
        Channel.All.Select(channel => channel.Value).Distinct(StringComparer.Ordinal)
            .Count().ShouldBe(Channel.All.Count);
    }

    /// <summary>
    /// Push sits outside the contactable channels on purpose and with a
    /// documented reason: push routing lives in device tokens, which have
    /// their own registration path and lifecycle. The cut is derived and
    /// checked, never erased. The day someone adds a channel to the
    /// vocabulary this goes red and forces the decision of which side it
    /// belongs on, which is the whole point.
    /// </summary>
    [Fact]
    public void Push_is_the_only_channel_of_the_vocabulary_no_contact_point_addresses()
    {
        string[] outsideContactPoints = [.. Channel.All
            .Select(channel => channel.Value)
            .Where(word => !ContactChannels.CanonicalValues.Contains(word, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];

        outsideContactPoints.ShouldBe(
            [Channel.Push.Value],
            "push está fora dos canais de ponto de contato de propósito e documentado, porque o "
            + "roteamento dele vive em token de dispositivo; um canal novo no vocabulário cai "
            + "aqui e obriga a decidir de que lado ele fica.");
    }

    /// <summary>
    /// The other direction, and a separate claim: the contactable channels are
    /// a cut of the vocabulary, so a word that lives only in the consent module
    /// would be a second vocabulary rather than a subset of the published one.
    /// </summary>
    [Fact]
    public void Every_contactable_channel_is_a_word_of_the_published_vocabulary()
        => ContactChannels.CanonicalValues.ShouldBeSubsetOf(
            Channel.All.Select(channel => channel.Value));
}
