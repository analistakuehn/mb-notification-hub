using System.Reflection;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.UnitTests.Dispatch;

/// <summary>
/// The surface this module publishes to the contexts that send notifications,
/// read from the compiled assembly rather than from the files that declare it.
/// <para>
/// Everything about shape here is a rule about what an adapter can reach from
/// one send. Nothing in this file claims that a provider is ever called or
/// that an attachment is ever composed; those are measured against real
/// adapters elsewhere. What it does claim is that the shape a caller fills
/// cannot carry a client of a cloud provider, cannot carry this module's own
/// internals, and cannot grow a dependency on another context without somebody
/// writing the reason down.
/// </para>
/// </summary>
public sealed class DispatchContractTests
{
    private static readonly string ContractNamespace = typeof(DispatchRequest).Namespace!;

    private static readonly Type[] Published =
        [.. typeof(DispatchRequest).Assembly
            .GetTypes()
            .Where(type => type.IsVisible
                && string.Equals(type.Namespace, ContractNamespace, StringComparison.Ordinal))
            .OrderBy(type => type.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Every type this boundary reaches that it does not publish itself and
    /// that the runtime does not ship, each with the reason it is admitted.
    /// The set is compared for exact equality in both directions, so reaching
    /// one more without recording it fails just as loudly as recording one the
    /// boundary stopped reaching.
    /// </summary>
    private static readonly (string Type, string Reason)[] RecordedAdmissions =
    [
        ("NotificationHub.Api.Modules.AttachmentManagement.Integration.V1.AcceptedAttachment", AcceptedSet),
        ("NotificationHub.Api.Modules.AttachmentManagement.Integration.V1.AcceptedAttachmentSet", AcceptedSet),
        ("NotificationHub.Api.Modules.TemplateManagement.Integration.V1.Channel", ChannelVocabulary),
        ("NotificationHub.SharedKernel.Result`1", ResultAxis),
        ("NotificationHub.SharedKernel.ResultErrorKind", ResultAxis),
    ];

    /// <summary>
    /// The attachment set a send carries, reused from the context that owns
    /// attachments instead of restated here. A second shape of the same
    /// concept would be two forms free to drift, and the drift would land on
    /// the one member a caller cannot restate for itself: the opaque handle of
    /// the accepted content, which only the owning module resolves. The
    /// neighbouring rule on that boundary is what keeps this admission neutral,
    /// because it is that rule, and not this one, which says the set names no
    /// store, no key, no address and no proof of the bytes.
    /// </summary>
    private const string AcceptedSet =
        "attachment set reused from the context that owns it, in its published form";

    /// <summary>
    /// The channel vocabulary, consumed from the context that owns templates.
    /// It predates the set above and settles the question the same way: which
    /// channels exist is that module's statement, and a copy here would be a
    /// second list of channels.
    /// </summary>
    private const string ChannelVocabulary =
        "channel vocabulary consumed from the context that owns templates";

    /// <summary>
    /// The result axis of the repository, which every published resolver in
    /// this module answers on. It is shared kernel rather than another
    /// context, so it carries no boundary of its own.
    /// </summary>
    private const string ResultAxis = "shared result axis of the repository";

    private static readonly string[] ExpectedAdmissions =
        [.. RecordedAdmissions.Select(entry => entry.Type).Order(StringComparer.Ordinal)];

    /// <summary>
    /// What the boundary is built out of. A contract that named a client of
    /// the object store, an entity of this module, a provider option or a
    /// bucket coordinate would hand every consumer of a send the inside of
    /// something, and the fitness function that forbids exactly that reads
    /// namespaces of foreign modules, which this module's own internals
    /// satisfy and a cloud client does not appear in at all.
    /// <para>
    /// So this reads the members instead, and follows every type it reaches
    /// that this repository compiles. The base library is a leaf: its own
    /// members are the runtime's business and following them would never end.
    /// </para>
    /// </summary>
    [Fact]
    public void The_boundary_is_built_from_itself_the_base_library_and_what_it_records()
    {
        HashSet<Type> reached = Reachable(Published);

        var foreign = reached
            .Where(type => !IsPublishedHere(type) && !IsBaseLibrary(type))
            .Select(Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // An entry with no reason is a name on a list, and the reason is the
        // only thing that lets a later reader tell a decision from a drift.
        RecordedAdmissions.ShouldAllBe(entry => entry.Reason.Length > 0);
        RecordedAdmissions
            .Select(entry => entry.Type)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(RecordedAdmissions.Length);

        foreign.ShouldBe(ExpectedAdmissions);
    }

    /// <summary>
    /// The walk has to be shown to reach the surface before the rule above
    /// means anything: a walk that found nothing satisfies any list of
    /// admissions and turns the whole rule green. The three types named here
    /// are reached by three different routes, so a walk that stopped following
    /// constructors, properties or foreign contracts loses one of them.
    /// </summary>
    [Fact]
    public void The_walk_reaches_what_a_send_is_made_of()
    {
        HashSet<Type> reached = Reachable(Published);

        Published.ShouldNotBeEmpty();

        // Through a constructor parameter of the send.
        reached.ShouldContain(typeof(DeliveryTarget));

        // Through the optional member the send carries the attachments on.
        reached.ShouldContain(typeof(AcceptedAttachmentSet));

        // Through a member of an admitted contract, which is the only route
        // that says the walk did not stop at the boundary of this module.
        reached.ShouldContain(typeof(AcceptedAttachment));
    }

    /// <summary>
    /// A send that names no attachment set carries none. The member is the
    /// last of the optional ones and defaults to nothing stated, which is why
    /// every caller written before it kept compiling and kept meaning the same
    /// send.
    /// <para>
    /// Null is the whole way to say a send carries no attachment, because a
    /// set with no members is a value the contract cannot hold at all.
    /// </para>
    /// </summary>
    [Fact]
    public void A_send_that_names_no_attachment_set_carries_none()
    {
        DispatchRequest request = Send(null);

        request.Attachments.ShouldBeNull();
    }

    /// <summary>
    /// A send carries the set it was handed, unchanged and in order. The
    /// contract neither copies nor reorders it, because the order is part of
    /// what was accepted and a send that reordered would deliver a set in an
    /// order nobody accepted.
    /// </summary>
    [Fact]
    public void A_send_carries_the_attachment_set_it_was_handed()
    {
        AcceptedAttachmentSet accepted = Set(Item("att_one"), Item("att_two"));

        DispatchRequest request = Send(accepted);

        request.Attachments.ShouldBeSameAs(accepted);
        request.Attachments!.Count.ShouldBe(2);
        request.Attachments[0].Reference.ShouldBe("att_one");
        request.Attachments[1].Reference.ShouldBe("att_two");
    }

    /// <summary>
    /// Two sends that carry the same attachments are the same send, and two
    /// that carry different ones are not. It is the question a caller actually
    /// puts: a request it built and a request it rebuilt are two instances by
    /// construction, and a member that answered about instances would report
    /// every such pair as different sends forever.
    /// <para>
    /// It is asked through the default comparer, which is the comparer the
    /// compiler closed the generated equality over, rather than through an
    /// assertion that walks a sequence. The set is a list as well as a value,
    /// and an assertion that enumerated would compare attachments and never
    /// ask the send.
    /// </para>
    /// </summary>
    [Fact]
    public void A_send_compares_by_the_attachment_set_it_carries()
    {
        DispatchRequest one = Send(Set(Item("att_one")));
        DispatchRequest same = Send(Set(Item("att_one")));

        Same(one, same).ShouldBeTrue();
        one.GetHashCode().ShouldBe(same.GetHashCode());

        Same(one, Send(Set(Item("att_two")))).ShouldBeFalse();
        Same(one, Send(Set(Item("att_one"), Item("att_two")))).ShouldBeFalse();
        Same(one, Send(null)).ShouldBeFalse();
    }

    private static DispatchRequest Send(AcceptedAttachmentSet? attachments)
        => new(
            new EmailDeliveryTarget("person@example.com"),
            new EmailMessage("subject", "preheader", "<p>body</p>", "body"),
            Attachments: attachments);

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
    /// Everything the published surface mentions, and everything those
    /// mentions mention in turn, for as long as the type is one this
    /// repository compiles.
    /// </summary>
    private static HashSet<Type> Reachable(IEnumerable<Type> roots)
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>(roots);

        while (pending.Count > 0)
        {
            Type current = pending.Pop();

            foreach (Type mentioned in MentionedTypes(current).SelectMany(Unwrap))
            {
                if (!seen.Add(mentioned) || IsBaseLibrary(mentioned))
                {
                    continue;
                }

                pending.Push(mentioned);
            }
        }

        return seen;
    }

    /// <summary>
    /// Every type a published member mentions: what a property carries, what a
    /// field holds, what a method answers with, and what it is handed. Members
    /// declared by the type itself, because a member inherited from the base
    /// library is the base library's business and reporting it would make the
    /// rule about the runtime instead of about the boundary.
    /// </summary>
    private static IEnumerable<Type> MentionedTypes(Type type)
    {
        const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (PropertyInfo property in type.GetProperties(Declared))
        {
            yield return property.PropertyType;
        }

        foreach (FieldInfo field in type.GetFields(Declared))
        {
            yield return field.FieldType;
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

    /// <summary>
    /// A generic type answers under the name of its definition, so one entry
    /// covers every closing of it and the name stays readable. A type
    /// parameter names no type at all: it stands for whatever a caller
    /// supplies, and that caller is bound by this same rule at its own site.
    /// </summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        Type bare = type.IsByRef || type.IsPointer || type.IsArray
            ? type.GetElementType() ?? type
            : type;

        if (bare.IsGenericParameter)
        {
            yield break;
        }

        yield return bare.IsGenericType ? bare.GetGenericTypeDefinition() : bare;

        if (!bare.IsGenericType)
        {
            yield break;
        }

        foreach (Type argument in bare.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }

    private static string Name(Type type) => type.FullName ?? type.Name;

    private static bool IsPublishedHere(Type type)
        => string.Equals(type.Namespace, ContractNamespace, StringComparison.Ordinal);

    /// <summary>
    /// The runtime and nothing else. Every assembly the base library ships
    /// answers to one of these names, and the assembly this module is compiled
    /// into answers to none of them, which is the whole distinction the rule
    /// above needs. A client of a cloud provider ships in an assembly of its
    /// own and answers to none of them either.
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
