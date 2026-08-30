using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using NotificationHub.Api.Composition;
using NotificationHub.Worker;

namespace NotificationHub.ArchTests;

/// <summary>
/// The equality a published contract record offers a consumer, asked of the
/// record instead of read off its declaration. The compiler closes the
/// generated comparison over the default comparer of each member's declared
/// type, and for an interface, an array, or a struct holding a reference, that
/// comparer dispatches on whatever instance the producer supplied: two lists
/// answer no, two collections that override equality answer yes, and two
/// boxes of the same immutable array answer yes. The equality of a published
/// contract is therefore chosen at run time by its producer, so the only
/// honest way to ask is to build two equal-by-member values and compare them.
/// </summary>
/// <remarks>
/// <para>
/// Two limits are declared here rather than closed, because neither is
/// reachable from a rule indexed by type.
/// </para>
/// <para>
/// A break can depend on the value instead of the type: a member declared as
/// an abstract contract compares by content under one concrete subtype and by
/// reference under another, and the whole record follows the instance it
/// happens to carry. This rule picks the first concrete subtype by ordinal
/// name and reports that verdict, so the other subtypes of that member stay
/// unmeasured and the census below is a floor, never a total.
/// </para>
/// <para>
/// A deliberate addition passes: a new record that breaks the promise, landed
/// in the same change as its inventory entry, is green here, and that is
/// correct. This gate stops the break nobody noticed, never the break somebody
/// decided on.
/// </para>
/// </remarks>
public sealed class PublishedContractEqualityTests
{
    private static readonly Assembly[] Production =
        [.. SolutionAssemblies.All, typeof(AssemblyMarker).Assembly];

    private const string ModuleNamespaceRoot = "NotificationHub.Api.Modules.";

    private const string ContractNamespaceSegment = "Integration";

    private const string PublishedVersionSegment = "V1";

    private const string ModulesRoot = "src/Platform.Api/Modules";

    /// <summary>
    /// The published contract records whose generated equality does not answer
    /// about content, each with the reason it stays that way. The set is
    /// compared for exact equality in both directions, so repairing a type
    /// without removing its line fails just as loudly as adding a break
    /// without adding one.
    /// </summary>
    private static readonly (string Contract, string Reason)[] RecordedBreaks =
    [
        ("Audit.AuditLink", UndecidedScope),
        ("Audit.AuditPeriodEvidence", UndecidedScope),
        ("Audit.AuditSubjectLinks", UndecidedScope),
        ("ContactConsent.RecipientSnapshot", UndecidedScope),
        ("Dispatch.ProviderWebhookRequest", UndecidedScope),
        ("Dispatch.PushMessage", ClosedHierarchy),
        ("Dispatch.VerifiedProviderWebhook", UndecidedScope),
        ("Notifications.NotificationAttemptEvidence", UndecidedScope),
        ("Notifications.NotificationClassVolume", UndecidedScope),
        ("Notifications.NotificationEvidence", UndecidedScope),
        ("Notifications.NotificationOutcomeSummary", UndecidedScope),
        ("Notifications.PolicyEvaluationEvidence", UndecidedScope),
        ("TemplateManagement.PolicyRuleResult.FilterChannels", ClosedHierarchy),
        ("TemplateManagement.PublishedTemplateLookup.Published", ClosedHierarchy),
    ];

    /// <summary>
    /// The break is real and the repair is not local: the type is a leaf of a
    /// closed record hierarchy, so turning it into a class turns its root and
    /// every sibling into one, and those siblings compare by content today.
    /// The entry records that constraint, not an oversight.
    /// </summary>
    private const string ClosedHierarchy =
        "leaf of a closed record hierarchy whose siblings compare by content today";

    /// <summary>
    /// The break is real and the module that owns the type has not been read
    /// under this question. Nothing here claims the type should stay as it is.
    /// </summary>
    private const string UndecidedScope =
        "owning module not yet reviewed under this question";

    private static readonly string[] ExpectedBrokenContracts =
        [.. RecordedBreaks.Select(entry => entry.Contract).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The walk has to be shown to reach its subject before any verdict below
    /// means anything: an empty walk satisfies an empty inventory and turns
    /// the whole rule green. The source tree answers here, and the compiled
    /// metadata answers there, so a namespace segment that stops matching
    /// empties one side and leaves the other intact.
    /// </summary>
    [Fact]
    public void Contract_discovery_reaches_every_module_that_publishes_one()
    {
        var fromSource = ModulesWithPublishedContractFolder();
        var fromMetadata = PublishedContractRecords()
            .Select(ModuleOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        fromSource.ShouldNotBeEmpty();
        fromMetadata.ShouldBe(fromSource);

        // Two contracts that collapse to one key would let one answer for the
        // other, and the set comparison of the census would never notice.
        var keys = PublishedContractRecords().Select(Key).ToArray();
        keys.Distinct(StringComparer.Ordinal).Count().ShouldBe(keys.Length);

        // Set equality above is satisfied by one record per module; the floor
        // is what says the walk reached the surface instead of its edge.
        PublishedContractRecords().Length.ShouldBeGreaterThan(3 * fromSource.Length);
    }

    /// <summary>
    /// A contract born outside the versioned segment is published all the same
    /// and never enters the walk above, so the rule would keep passing over a
    /// surface it no longer covers. The contract folder of a module that
    /// publishes one carries the version segment and nothing else.
    /// </summary>
    [Fact]
    public void Published_contract_carries_no_type_outside_the_versioned_segment()
    {
        var modules = ModulesWithPublishedContractFolder();
        var contractRoots = modules
            .Select(module => ModuleNamespaceRoot + module + "." + ContractNamespaceSegment)
            .ToArray();

        var outsideTheVersion = Production
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsVisible && type.Namespace is not null)
            .Where(type => contractRoots.Any(root => IsInNamespace(type.Namespace!, root)))
            .Where(type => !contractRoots.Any(root =>
                IsInNamespace(type.Namespace!, root + "." + PublishedVersionSegment)))
            .Where(HasPublicMember)
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        outsideTheVersion.ShouldBeEmpty();
    }

    /// <summary>
    /// Two values equal member by member, built from distinct instances, and
    /// the question put to the comparer the compiler actually closed over.
    /// Reading the member list and inferring would answer about the
    /// declaration; this answers about the type.
    /// </summary>
    [Fact]
    public void Contracts_that_break_content_equality_are_exactly_the_recorded_ones()
    {
        Type[] contracts = PublishedContractRecords();
        var vacuous = new List<string>();
        var broken = new List<string>();

        foreach (Type contract in contracts)
        {
            var left = BuildValue(contract, 0);
            var right = BuildValue(contract, 0);

            foreach (FieldInfo member in StateFields(contract))
            {
                if (member.GetValue(left) is null && member.GetValue(right) is null)
                {
                    vacuous.Add(Key(contract) + " -> " + member.Name);
                }
            }

            if (!DefaultComparerSaysEqual(contract, left, right))
            {
                broken.Add(Key(contract));
            }
        }

        // A member left unset on both sides compares equal to itself, and the
        // contract then reports as sound for a reason that has nothing to do
        // with the contract. It is the quietest way this rule can empty out.
        vacuous.ShouldBeEmpty();

        // An entry with no reason is a name on a list, and the reason is the
        // only thing that lets a later reader tell a constraint from an
        // oversight. A repeated name would let one entry answer for two.
        RecordedBreaks.ShouldAllBe(entry => entry.Reason.Length > 0);
        RecordedBreaks
            .Select(entry => entry.Contract)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(RecordedBreaks.Length);

        broken
            .Order(StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(ExpectedBrokenContracts);
    }

    /// <summary>
    /// The published contract records of every module, each concrete: an
    /// abstract record has no value of its own to compare, and its verdict is
    /// the verdict of whichever leaf a caller holds, which the leaves below
    /// already answer one by one.
    /// </summary>
    private static Type[] PublishedContractRecords() => Contracts;

    private static readonly Type[] Contracts =
        [.. Production
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsVisible: true, IsAbstract: false, Namespace: not null })
            .Where(type => IsPublishedContractNamespace(type.Namespace!))
            .Where(IsRecord)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)];

    /// <summary>
    /// Owning module plus the type path inside its namespace, so a nested
    /// contract keeps the name that identifies it and two modules that name a
    /// contract alike never answer for each other.
    /// </summary>
    private static string Key(Type contract)
        => ModuleOf(contract)
            + "."
            + contract.FullName![(contract.Namespace!.Length + 1)..].Replace('+', '.');

    private static string ModuleOf(Type contract)
        => contract.Namespace![ModuleNamespaceRoot.Length..].Split('.')[0];

    private static bool IsPublishedContractNamespace(string candidate)
    {
        if (!candidate.StartsWith(ModuleNamespaceRoot, StringComparison.Ordinal))
        {
            return false;
        }

        var withoutRoot = candidate[ModuleNamespaceRoot.Length..];
        var separator = withoutRoot.IndexOf('.', StringComparison.Ordinal);

        return separator >= 0
            && IsInNamespace(
                withoutRoot[(separator + 1)..],
                ContractNamespaceSegment + "." + PublishedVersionSegment);
    }

    private static bool IsInNamespace(string candidate, string root)
        => candidate.Equals(root, StringComparison.Ordinal)
            || candidate.StartsWith(root + ".", StringComparison.Ordinal);

    private static bool IsRecord(Type type)
        => type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null
            || (type.IsValueType
                && type.GetMethod(
                    "PrintMembers",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    [typeof(StringBuilder)]) is not null);

    private static bool HasPublicMember(Type type)
        => type.GetMembers(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly).Length > 0;

    /// <summary>
    /// Every field that carries state, including the private backing fields a
    /// base record declares: reflection stops at the first base type for a
    /// private member, and a derived record whose only state lives up the
    /// chain would otherwise be compared with nothing set on either side.
    /// </summary>
    private static FieldInfo[] StateFields(Type type)
    {
        var fields = new List<FieldInfo>();

        for (Type? current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            fields.AddRange(current
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(field => !field.IsLiteral)
                .OrderBy(field => field.Name, StringComparer.Ordinal));
        }

        return [.. fields];
    }

    private static bool DefaultComparerSaysEqual(Type contract, object left, object right)
    {
        Type comparerType = typeof(EqualityComparer<>).MakeGenericType(contract);
        var comparer = comparerType
            .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        MethodInfo equals = comparerType.GetMethod("Equals", [contract, contract])!;

        return (bool)equals.Invoke(comparer, [left, right])!;
    }

    /// <summary>
    /// A value of the requested type, fresh on every call, so two calls give
    /// equal content in distinct instances. A type whose only construction
    /// door yields canonical instances is the one exception: for it, reference
    /// equality is content equality, and handing out the canonical instance is
    /// what a producer can actually do. The neighbouring rule on that type
    /// keeps that premise true.
    /// </summary>
    private static object BuildValue(Type type, int depth)
    {
        if (depth > MaxFabricationDepth)
        {
            throw new InvalidOperationException(
                "Fabricating '" + type.FullName + "' exceeded the nesting budget.");
        }

        Type? underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return BuildValue(underlying, depth);
        }

        if (type == typeof(string))
        {
            return new string(ProbeCharacter, ProbeLength);
        }

        if (type == typeof(JsonElement))
        {
            return JsonDocument.Parse(ProbeJson).RootElement;
        }

        if (type.IsEnum)
        {
            return ProbeEnumValue(type);
        }

        var scalar = ScalarValue(type);
        if (scalar is not null)
        {
            return scalar;
        }

        if (type.IsArray)
        {
            Type element = type.GetElementType()!;
            Array buffer = Array.CreateInstance(element, 1);
            buffer.SetValue(BuildValue(element, depth + 1), 0);
            return buffer;
        }

        if (type.IsGenericType)
        {
            var shaped = ShapedValue(type, depth);
            if (shaped is not null)
            {
                return shaped;
            }
        }

        if (!type.IsValueType)
        {
            var canonical = CanonicalInstance(type);
            if (canonical is not null)
            {
                return canonical;
            }
        }

        Type concrete = type.IsAbstract || type.IsInterface ? FirstConcreteSubtype(type) : type;
        var instance = RuntimeHelpers.GetUninitializedObject(concrete);

        foreach (FieldInfo field in StateFields(concrete))
        {
            field.SetValue(instance, BuildValue(field.FieldType, depth + 1));
        }

        return instance;
    }

    private static object? ShapedValue(Type type, int depth)
    {
        Type definition = type.GetGenericTypeDefinition();
        Type[] arguments = type.GetGenericArguments();

        if (definition == typeof(ReadOnlyMemory<>) || definition == typeof(Memory<>))
        {
            Array buffer = Array.CreateInstance(arguments[0], 1);
            buffer.SetValue(BuildValue(arguments[0], depth + 1), 0);
            return Activator.CreateInstance(type, [buffer])!;
        }

        if (definition == typeof(IReadOnlyDictionary<,>)
            || definition == typeof(IDictionary<,>)
            || definition == typeof(Dictionary<,>))
        {
            return Filled(typeof(Dictionary<,>).MakeGenericType(arguments), arguments, depth);
        }

        if (definition == typeof(IReadOnlySet<>) || definition == typeof(ISet<>) || definition == typeof(HashSet<>))
        {
            return Filled(typeof(HashSet<>).MakeGenericType(arguments), arguments, depth);
        }

        if (definition == typeof(IReadOnlyList<>)
            || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(IList<>)
            || definition == typeof(ICollection<>)
            || definition == typeof(IEnumerable<>)
            || definition == typeof(List<>))
        {
            return Filled(typeof(List<>).MakeGenericType(arguments), arguments, depth);
        }

        return null;
    }

    private static object Filled(Type closed, Type[] arguments, int depth)
    {
        var collection = Activator.CreateInstance(closed)!;
        var entry = arguments.Select(argument => BuildValue(argument, depth + 1)).ToArray();
        closed.GetMethod("Add", arguments)!.Invoke(collection, entry);

        return collection;
    }

    private static object? ScalarValue(Type type)
    {
        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(char))
        {
            return ProbeCharacter;
        }

        if (type.IsPrimitive)
        {
            return Convert.ChangeType(ProbeNumber, type, CultureInfo.InvariantCulture);
        }

        if (type == typeof(decimal))
        {
            return (decimal)ProbeNumber;
        }

        if (type == typeof(Guid))
        {
            return ProbeGuid;
        }

        if (type == typeof(DateTimeOffset))
        {
            return ProbeInstant;
        }

        if (type == typeof(DateTime))
        {
            return ProbeInstant.UtcDateTime;
        }

        if (type == typeof(TimeSpan))
        {
            return TimeSpan.FromMinutes(ProbeNumber);
        }

        if (type == typeof(DateOnly))
        {
            return DateOnly.FromDateTime(ProbeInstant.UtcDateTime);
        }

        return type == typeof(TimeOnly) ? TimeOnly.FromDateTime(ProbeInstant.UtcDateTime) : null;
    }

    private static object ProbeEnumValue(Type type)
    {
        Array values = Enum.GetValues(type);
        for (var index = 0; index < values.Length; index++)
        {
            var candidate = values.GetValue(index)!;
            if (Convert.ToInt64(candidate, CultureInfo.InvariantCulture) != 0)
            {
                return candidate;
            }
        }

        return values.GetValue(0)!;
    }

    private static object? CanonicalInstance(Type type)
        => type
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.FieldType == type)
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .Select(field => field.GetValue(null))
            .FirstOrDefault(value => value is not null);

    private static Type FirstConcreteSubtype(Type contract)
        => Production
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && !type.IsInterface && contract.IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No concrete subtype of '" + contract.FullName + "' exists to fabricate.");

    private static string[] ModulesWithPublishedContractFolder()
        => [.. Directory
            .EnumerateDirectories(ModulesDirectory())
            .Where(module => Directory.Exists(
                Path.Combine(module, ContractNamespaceSegment, PublishedVersionSegment)))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)];

    private static string ModulesDirectory()
    {
        var directory = Path.Combine(
            FindSolutionRoot(),
            ModulesRoot.Replace('/', Path.DirectorySeparatorChar));

        return Directory.Exists(directory)
            ? directory
            : throw new DirectoryNotFoundException(
                "The module source anchor '" + ModulesRoot + "' no longer exists.");
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the solution root.");
    }

    private const int MaxFabricationDepth = 8;

    private const char ProbeCharacter = 'p';

    private const int ProbeLength = 5;

    private const int ProbeNumber = 7;

    private const string ProbeJson = "{\"probe\":1}";

    private static readonly Guid ProbeGuid = new("2f1c6a8e-4d3b-4a71-9c55-8e0f1a2b3c4d");

    private static readonly DateTimeOffset ProbeInstant =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
}
