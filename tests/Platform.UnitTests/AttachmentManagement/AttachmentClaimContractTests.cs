using System.Globalization;
using System.Reflection;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// The surface this module publishes to the context that sends notifications,
/// read from the compiled assembly rather than from the files that declare it.
/// <para>
/// Everything here is a rule about shape, and that is deliberate: what a claim
/// writes and what a check reads are measured against real stores elsewhere,
/// and nothing in this file claims that a set is ever claimed or that a release
/// is ever read. What it does claim is that the shape an implementation has to
/// fill cannot carry a coordinate, cannot carry the proof of the bytes, cannot
/// report an acceptance with nothing accepted, and cannot let a set through by
/// default.
/// </para>
/// </summary>
public sealed class AttachmentClaimContractTests
{
    private static readonly string ContractNamespace = typeof(IAttachmentClaim).Namespace!;

    private static readonly Type[] Published =
        [.. typeof(IAttachmentClaim).Assembly
            .GetTypes()
            .Where(type => type.IsVisible
                && string.Equals(type.Namespace, ContractNamespace, StringComparison.Ordinal))
            .OrderBy(type => type.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Every type the boundary carries, named here so that publishing one more
    /// is an act somebody performed rather than a file somebody added. The
    /// walk is held against this list in both directions, so a type that
    /// stopped being visible fails just as loudly as a type that started.
    /// </summary>
    [Fact]
    public void The_boundary_publishes_exactly_the_types_it_was_published_with()
    {
        Published
            .Select(type => type.Name)
            .ToArray()
            .ShouldBe(
            [
                nameof(AcceptedAttachment),

                // The way to the bytes, and the two types that answer for one
                // reading of them. It is published here because resolving the
                // handle is this module's own act: the alternative is a
                // consumer reaching the custody itself, which is a second
                // authority over which bytes an accepted attachment is.
                nameof(AcceptedAttachmentContent),
                nameof(AcceptedAttachmentContentStatus),
                nameof(AcceptedAttachmentSet),
                nameof(AttachmentClaimOutcome),
                nameof(AttachmentClaimRequest),
                nameof(AttachmentClaimStatus),
                nameof(AttachmentEnvelopeVerdict),
                nameof(AttachmentReferences),
                nameof(AttachmentReleaseVerdict),

                // The witness of what a send actually put on the wire, and the
                // three types that carry one settlement of it. It is published
                // here for the same reason the way to the bytes is: the
                // released side of the comparison is the digest on the
                // generation row, it goes nowhere, and a consumer handed that
                // digest instead would be holding the proof of the bytes in a
                // form every message and every log line could copy.
                nameof(AttachmentSubmissionVerdict),
                nameof(IAcceptedAttachmentContent),
                nameof(IAttachmentClaim),
                nameof(IAttachmentEnvelopeCheck),
                nameof(IAttachmentReleaseCheck),
                nameof(IAttachmentSubmissionWitness),
                nameof(SubmittedAttachmentBytes),
            ]);
    }

    /// <summary>
    /// Every value the boundary carries, and every word its two closed
    /// vocabularies admit. This is the rule that a coordinate, a managed key,
    /// an address or a digest has to get past to reach a consumer, and it does
    /// not ask whether a member looks like one: it asks whether the member was
    /// published, which is a question a name cannot dodge.
    /// </summary>
    [Fact]
    public void The_boundary_carries_exactly_the_values_it_was_published_with()
    {
        var members = Published
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => type.Name + "." + property.Name))
            .Concat(Published
                .Where(type => type.IsEnum)
                .SelectMany(type => Enum.GetNames(type).Select(name => type.Name + "." + name)))
            .Concat(Published
                .Where(type => type.IsInterface)
                .SelectMany(type => type
                    .GetMethods()
                    .Select(method => type.Name + "." + method.Name)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        members.ShouldBe(
        [
            // What was accepted, and nothing that proves it. The digest and
            // the algorithm that say which bytes these are stay on the
            // generation record; the handle below is what reaches them, and
            // resolving it is this module's own act.
            "AcceptedAttachment.ContentIdentity",
            "AcceptedAttachment.Length",
            "AcceptedAttachment.MediaType",
            "AcceptedAttachment.Name",
            "AcceptedAttachment.Reference",

            // One reading of the content: the bytes and whether there are any.
            // The stream is the content itself and not a way to it, which is
            // the distinction that keeps a coordinate off this surface: a
            // consumer holding it can read these bytes and can reach nothing
            // else, and it learns no store, no key and no generation on the
            // way.
            "AcceptedAttachmentContent.Status",
            "AcceptedAttachmentContent.Stream",

            // Two words for the reading, and the refusal is one of them for
            // every way of not yielding bytes: a handle this module never
            // minted, a record that is gone and a custody that cannot be
            // reached are three events on this side and one on the caller's.
            "AcceptedAttachmentContentStatus.Opened",
            "AcceptedAttachmentContentStatus.Unavailable",

            // The set is a list and answers as one. Its members are the two a
            // list has, and the items inside it are the values above.
            "AcceptedAttachmentSet.Count",
            "AcceptedAttachmentSet.Item",

            "AttachmentClaimOutcome.Accepted",
            "AttachmentClaimOutcome.Status",
            "AttachmentClaimRequest.Application",
            "AttachmentClaimRequest.ClaimKey",
            "AttachmentClaimRequest.NotificationId",
            "AttachmentClaimRequest.References",

            // Two words for the refusals and one for the acceptance. A third
            // refusal would tell a caller which of the two states it guessed
            // at, and that is the distinction this vocabulary exists to
            // withhold.
            "AttachmentClaimStatus.ClaimKeyConflict",
            "AttachmentClaimStatus.Claimed",
            "AttachmentClaimStatus.NotClaimable",

            // Two words for the capacity, and the refusal is one of them for
            // both ways of exceeding it: a count and a sum are different rules
            // over the same snapshot, and the caller does the same thing about
            // either.
            "AttachmentEnvelopeVerdict.Exceeded",
            "AttachmentEnvelopeVerdict.WithinEnvelope",

            "AttachmentReferences.Count",
            "AttachmentReferences.Item",

            "AttachmentReleaseVerdict.Deliverable",
            "AttachmentReleaseVerdict.Unavailable",
            "AttachmentReleaseVerdict.Withheld",

            // Three words for what the bytes that left turned out to be, and
            // the refusal is not one of them: a comparison that could not be
            // made is the absence of a statement and reads as the zero, so a
            // stand-in nobody told what to answer cannot certify a submission.
            "AttachmentSubmissionVerdict.Divergent",
            "AttachmentSubmissionVerdict.Matched",
            "AttachmentSubmissionVerdict.Unavailable",

            "IAcceptedAttachmentContent.OpenAsync",
            "IAttachmentClaim.ClaimAsync",
            "IAttachmentEnvelopeCheck.Measure",
            "IAttachmentReleaseCheck.VerifyAsync",
            "IAttachmentSubmissionWitness.SettleAsync",

            // What one member measured on its way out, and every value here
            // travels inwards only. The digest is the caller's own measurement
            // of bytes the caller was already holding, so handing it over
            // publishes nothing new; what this surface still refuses to carry
            // is the recorded digest, which never leaves the row it sits on.
            "SubmittedAttachmentBytes.ContentIdentity",
            "SubmittedAttachmentBytes.Digest",
            "SubmittedAttachmentBytes.Length",
        ]);
    }

    /// <summary>
    /// What the boundary is built out of. A contract that named an entity, a
    /// mapping, a client of the storage provider or a type of this module's
    /// own domain would make every consumer of it depend on the inside of this
    /// module, and the fitness function that forbids exactly that reads
    /// namespaces, which a type reached through a member of a published record
    /// still satisfies.
    /// <para>
    /// So this reads the members instead. Everything a published member
    /// mentions is either published beside it or comes from the base library,
    /// and the one non-trivial admission is the transaction of the caller,
    /// which is the whole point of a contract that writes inside somebody
    /// else's unit of work.
    /// </para>
    /// </summary>
    [Fact]
    public void The_boundary_is_built_from_itself_and_the_base_library_alone()
    {
        var foreign = Published
            .SelectMany(MentionedTypes)
            .SelectMany(Unwrap)
            .Where(type => !IsPublishedHere(type) && !IsBaseLibrary(type))
            .Select(type => type.FullName ?? type.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreign.ShouldBeEmpty();

        // The walk has to be shown to reach the members before the emptiness
        // above says anything, because a walk that found nothing is empty for
        // a reason that has nothing to do with the boundary.
        Published.SelectMany(MentionedTypes).ShouldNotBeEmpty();
        Published
            .SelectMany(MentionedTypes)
            .SelectMany(Unwrap)
            .ShouldContain(typeof(AcceptedAttachment));
    }

    /// <summary>
    /// The set answers about what it carries, which is the only answer that
    /// makes the comparison a consumer performs mean what it looks like: the
    /// set it submitted against the set it stored is two instances by
    /// construction, and a set that answered about instances would report them
    /// as different sets forever.
    /// </summary>
    [Fact]
    public void An_accepted_set_compares_by_what_it_carries()
    {
        AcceptedAttachmentSet left = Set(Item("att_one"), Item("att_two"));
        AcceptedAttachmentSet right = Set(Item("att_one"), Item("att_two"));

        left.ShouldNotBeSameAs(right);
        Same(left, right).ShouldBeTrue();
        left.GetHashCode().ShouldBe(right.GetHashCode());
    }

    /// <summary>
    /// One member at a time, because a comparison that ignored a member would
    /// still report two identical sets as equal and would look exactly like
    /// the rule above passing.
    /// </summary>
    [Fact]
    public void An_accepted_set_differs_when_any_single_member_differs()
    {
        AcceptedAttachmentSet accepted = Set(Item("att_one"));
        AcceptedAttachment item = Item("att_one");

        Same(Set(item with { Reference = "att_other" }), accepted).ShouldBeFalse();
        Same(Set(item with { ContentIdentity = "aci_other" }), accepted).ShouldBeFalse();
        Same(Set(item with { Name = "other.pdf" }), accepted).ShouldBeFalse();
        Same(Set(item with { MediaType = "text/plain" }), accepted).ShouldBeFalse();
        Same(Set(item with { Length = 41 }), accepted).ShouldBeFalse();
    }

    /// <summary>
    /// The order is part of the snapshot, so the same attachments in another
    /// order are another snapshot. A comparison that sorted or that counted
    /// would report the two as one and let a later attempt send the set in an
    /// order nobody accepted.
    /// </summary>
    [Fact]
    public void An_accepted_set_is_not_the_same_set_in_another_order()
    {
        AcceptedAttachmentSet accepted = Set(Item("att_one"), Item("att_two"));

        Same(Set(Item("att_two"), Item("att_one")), accepted).ShouldBeFalse();
        Same(Set(Item("att_one")), accepted).ShouldBeFalse();
    }

    /// <summary>
    /// The value is built from a copy, so the caller's array is the caller's
    /// business afterwards. A snapshot that kept the array it was handed would
    /// be a snapshot the producer of it could still rewrite.
    /// </summary>
    [Fact]
    public void An_accepted_set_keeps_what_it_was_built_from_and_not_where_it_came_from()
    {
        AcceptedAttachment[] source = [Item("att_one")];
        AcceptedAttachmentSet accepted = AcceptedAttachmentSet.Of(source);

        source[0] = Item("att_rewritten");

        accepted.Count.ShouldBe(1);
        accepted[0].Reference.ShouldBe("att_one");
    }

    /// <summary>
    /// The shapes the document that stores this snapshot cannot hold. Each of
    /// them reads back, on the far side, as a document nobody can trust, and
    /// refusing them where the snapshot is built is what keeps that reader
    /// from ever meeting one.
    /// </summary>
    [Fact]
    public void An_accepted_set_refuses_a_shape_its_document_cannot_hold()
    {
        AcceptedAttachment item = Item("att_one");

        Should.Throw<ArgumentException>(() => AcceptedAttachmentSet.Of([]));
        Should.Throw<ArgumentException>(() => Set(item, item));
        Should.Throw<ArgumentException>(() => Set(item with { Reference = " " }));
        Should.Throw<ArgumentException>(() => Set(item with { ContentIdentity = "" }));
        Should.Throw<ArgumentException>(() => Set(item with { Name = " " }));
        Should.Throw<ArgumentException>(() => Set(item with { MediaType = " " }));
        Should.Throw<ArgumentException>(() => Set(item with { Length = -1 }));

        // A length of zero is a file with no bytes, which is a file. It is the
        // neighbour of the refusal above and it is here so that the refusal
        // cannot quietly grow into it.
        Set(item with { Length = 0 }).Count.ShouldBe(1);
    }

    /// <summary>
    /// What an accepted attachment says when something writes it out. A record
    /// renders every public member it has, and three of these are values this
    /// module keeps off a log line: the released name, the released media type
    /// and the released length. The opaque reference stays, because it is what
    /// this module already logs and the only thing that makes the rendering
    /// worth having.
    /// </summary>
    [Fact]
    public void An_accepted_attachment_renders_without_the_values_it_was_released_under()
    {
        AcceptedAttachment item = Item("att_one");

        var rendered = item.ToString();

        rendered.ShouldContain("att_one");
        rendered.Contains(item.Name, StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        rendered.Contains(item.MediaType, StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        rendered.Contains(
            item.Length.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal).ShouldBeFalse();
        rendered.Contains(item.ContentIdentity, StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    /// <summary>
    /// What a submitted measurement says when something writes it out. A record
    /// renders every public member it has, and all three of these are values
    /// that must not reach a line: the handle is producer-adjacent, and the
    /// digest and the length describe content this side is not allowed to
    /// publish. Nothing of the member survives the rendering, which is why the
    /// stand-in has no correlator in it either.
    /// </summary>
    [Fact]
    public void A_submitted_measurement_renders_without_the_handle_the_length_or_the_digest()
    {
        var digest = new byte[] { 0xAB, 0xCD, 0xEF, 0x01 };
        var submitted = new SubmittedAttachmentBytes
        {
            ContentIdentity = "aci_" + Guid.NewGuid().ToString("N"),
            Length = 4_099,
            Digest = digest,
        };

        var rendered = submitted.ToString();

        rendered.ShouldBe(SubmittedAttachmentBytes.Redacted);
        rendered.Contains(submitted.ContentIdentity, StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse();
        rendered.Contains(
            submitted.Length.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal).ShouldBeFalse();
        foreach (var spelling in new[]
        {
            Convert.ToHexString(digest),
            Convert.ToHexString(digest).ToLowerInvariant(),
            Convert.ToBase64String(digest),
        })
        {
            rendered.Contains(spelling, StringComparison.Ordinal).ShouldBeFalse();
        }
    }

    /// <summary>
    /// A submitted measurement answers about what it carries, and the digest
    /// is the member that makes the question worth asking: it arrives as a
    /// region of memory, and the comparison the compiler writes for one of
    /// those answers about the buffer rather than about the bytes in it.
    /// <para>
    /// Two arrays are built here on purpose. A single array shared by both
    /// values compares equal under either rule, so an assertion that reused
    /// one would pass over exactly the defect this closes.
    /// </para>
    /// </summary>
    [Fact]
    public void A_submitted_measurement_compares_by_the_bytes_of_its_digest()
    {
        var handle = "aci_" + Guid.NewGuid().ToString("N");
        SubmittedAttachmentBytes left = Submitted(handle, 4_099, [1, 2, 3, 4]);
        SubmittedAttachmentBytes right = Submitted(handle, 4_099, [1, 2, 3, 4]);

        // Asked through the default comparer of the member's own type, which
        // is the comparer the compiler would have closed the generated
        // equality over. An assertion library walks the two regions element by
        // element and never puts the question to the type at all, so it
        // reports these two as the same and the tripwire stops tripping.
        Same(left.Digest, right.Digest).ShouldBeFalse();
        Same(left, right).ShouldBeTrue();
        left.GetHashCode().ShouldBe(right.GetHashCode());

        Same(left, Submitted(handle, 4_099, [1, 2, 3, 5])).ShouldBeFalse();
        Same(left, Submitted(handle, 4_099, [1, 2, 3])).ShouldBeFalse();
        Same(left, Submitted(handle, 4_100, [1, 2, 3, 4])).ShouldBeFalse();
        Same(left, Submitted("aci_other", 4_099, [1, 2, 3, 4])).ShouldBeFalse();
    }

    /// <summary>
    /// A verdict nobody produced is the absence of a statement. It is the one
    /// property of this vocabulary that a stand-in can defeat by doing nothing
    /// at all: a default that meant agreement would let a witness that was
    /// never asked certify every submission this hub makes.
    /// </summary>
    [Fact]
    public void A_submission_verdict_nobody_produced_certifies_nothing()
        => default(AttachmentSubmissionVerdict).ShouldBe(AttachmentSubmissionVerdict.Unavailable);

    [Fact]
    public void A_manifest_compares_by_what_it_carries_and_keeps_its_order()
    {
        AttachmentReferences manifest = AttachmentReferences.Of(["att_one", "att_two"]);

        Same(manifest, AttachmentReferences.Of(["att_one", "att_two"])).ShouldBeTrue();
        manifest.GetHashCode().ShouldBe(AttachmentReferences.Of(["att_one", "att_two"]).GetHashCode());
        Same(manifest, AttachmentReferences.Of(["att_two", "att_one"])).ShouldBeFalse();
        Same(manifest, AttachmentReferences.Of(["att_one"])).ShouldBeFalse();
        manifest[0].ShouldBe("att_one");
        manifest[1].ShouldBe("att_two");
    }

    [Fact]
    public void A_manifest_keeps_what_it_was_built_from_and_not_where_it_came_from()
    {
        string[] source = ["att_one"];
        AttachmentReferences manifest = AttachmentReferences.Of(source);

        source[0] = "att_rewritten";

        manifest.Count.ShouldBe(1);
        manifest[0].ShouldBe("att_one");
    }

    /// <summary>
    /// A list that names nothing, a list with blank text in it and a list that
    /// names the same attachment twice. None of them is data a producer can
    /// send, because the surface that admits a manifest refuses all three
    /// before a request is hashed, so a list that reaches here in one of those
    /// shapes is a defect on this side of the boundary.
    /// </summary>
    [Fact]
    public void A_manifest_refuses_a_list_that_names_nothing_blank_or_twice()
    {
        Should.Throw<ArgumentException>(() => AttachmentReferences.Of([]));
        Should.Throw<ArgumentException>(() => AttachmentReferences.Of(["att_one", " "]));
        Should.Throw<ArgumentException>(() => AttachmentReferences.Of(["att_one", "att_one"]));
        Should.Throw<ArgumentNullException>(() => AttachmentReferences.Of(null!));

        // Two references that differ only in case are two references, because
        // the comparison that decides it is ordinal everywhere this value
        // travels, including the document that stores the snapshot.
        AttachmentReferences.Of(["att_one", "ATT_ONE"]).Count.ShouldBe(2);
    }

    /// <summary>
    /// A refusal carries no snapshot and an acceptance carries one, and
    /// neither can be built the other way round. A caller that read the status
    /// alone and proceeded would be reading an acceptance with nothing
    /// accepted, and that outcome has no constructor.
    /// </summary>
    [Fact]
    public void A_claim_outcome_cannot_report_an_acceptance_with_nothing_accepted()
    {
        AcceptedAttachmentSet accepted = Set(Item("att_one"));

        AttachmentClaimOutcome claimed = AttachmentClaimOutcome.Claimed(accepted);
        claimed.Status.ShouldBe(AttachmentClaimStatus.Claimed);
        Same(claimed.Accepted!, accepted).ShouldBeTrue();

        AttachmentClaimOutcome refused =
            AttachmentClaimOutcome.Refused(AttachmentClaimStatus.NotClaimable);
        refused.Status.ShouldBe(AttachmentClaimStatus.NotClaimable);
        refused.Accepted.ShouldBeNull();

        Should.Throw<ArgumentOutOfRangeException>(
            () => AttachmentClaimOutcome.Refused(AttachmentClaimStatus.Claimed));
        Should.Throw<ArgumentNullException>(() => AttachmentClaimOutcome.Claimed(null!));
    }

    /// <summary>
    /// The answer nobody produced. A verdict read from a value nothing set,
    /// and a stand-in that was never told what to answer, both land on the
    /// first word of the axis, so the first word is the one that stops the
    /// set instead of the one that lets it through.
    /// </summary>
    [Fact]
    public void The_answer_nobody_produced_stops_the_set()
    {
        default(AttachmentReleaseVerdict).ShouldBe(AttachmentReleaseVerdict.Unavailable);
        default(AttachmentClaimStatus).ShouldBe(AttachmentClaimStatus.NotClaimable);
        default(AttachmentEnvelopeVerdict).ShouldBe(AttachmentEnvelopeVerdict.Exceeded);
    }

    /// <summary>
    /// The comparison the type offers, asked by name. Both published
    /// collections enumerate what they carry, so an assertion written as a
    /// comparison of two of them binds the overload that walks elements and
    /// answers without ever reaching the type: it reports content equality
    /// that the type does not offer, and a set that compared by instance, or
    /// by one member of each item, passes it.
    /// </summary>
    private static SubmittedAttachmentBytes Submitted(string handle, long length, byte[] digest)
        => new()
        {
            ContentIdentity = handle,
            Length = length,
            Digest = digest,
        };

    private static bool Same<T>(T left, T right)
        => EqualityComparer<T>.Default.Equals(left, right);

    private static AcceptedAttachmentSet Set(params AcceptedAttachment[] items)
        => AcceptedAttachmentSet.Of(items);

    private static AcceptedAttachment Item(string reference)
        => new()
        {
            Reference = reference,
            ContentIdentity = "aci_" + reference,
            Name = "invoice.pdf",
            MediaType = "application/pdf",
            Length = 42,
        };

    /// <summary>
    /// Every type a published member mentions: what a property carries, what a
    /// method answers with, and what it is handed. Members declared by the type
    /// itself, because a member inherited from the base library is the base
    /// library's business and reporting it would make the rule about the
    /// runtime instead of about the boundary.
    /// </summary>
    private static IEnumerable<Type> MentionedTypes(Type type)
    {
        const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (PropertyInfo property in type.GetProperties(Declared))
        {
            yield return property.PropertyType;
        }

        foreach (MethodInfo method in type.GetMethods(Declared))
        {
            yield return method.ReturnType;
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (ConstructorInfo constructor in type.GetConstructors(Declared))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        Type bare = type.IsByRef || type.IsPointer || type.IsArray
            ? type.GetElementType() ?? type
            : type;

        yield return bare;

        if (!bare.IsGenericType)
        {
            yield break;
        }

        foreach (Type argument in bare.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }

    private static bool IsPublishedHere(Type type)
        => string.Equals(type.Namespace, ContractNamespace, StringComparison.Ordinal);

    /// <summary>
    /// The runtime and nothing else. Every assembly the base library ships
    /// answers to one of these names, and the assembly this module is compiled
    /// into answers to none of them, which is the whole distinction the rule
    /// above needs.
    /// </summary>
    private static bool IsBaseLibrary(Type type)
    {
        var assembly = type.Assembly.GetName().Name ?? string.Empty;

        return assembly.StartsWith("System.", StringComparison.Ordinal)
            || string.Equals(assembly, "System", StringComparison.Ordinal)
            || string.Equals(assembly, "mscorlib", StringComparison.Ordinal)
            || string.Equals(assembly, "netstandard", StringComparison.Ordinal);
    }
}
