using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement;
using NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

namespace NotificationHub.ArchTests;

/// <summary>
/// Which governed effects of this module name an application on the audit row
/// they write. The layout family leaves the field absent and every other audit
/// writer of the module fills it, and that split is a decision rather than an
/// oversight: the field holds one value, a layout is pinned by the templates
/// of many applications, and an effect on a layout that five applications pin
/// has no single application to name. This rule holds the split in place,
/// because the shape of the omission invites a repair that looks obvious and
/// is wrong, which is to pick one application and write it. That repair leaves
/// the trail looking complete and reading false, and nothing downstream would
/// report it.
/// </summary>
/// <remarks>
/// <para>
/// Both sides are read off compiled code and never off a list written here, so
/// the rule cannot agree with itself. The observed side is IL rather than
/// source text because every one of these writers builds its entry inside an
/// async method, so the construction and the assignment sit in a state machine
/// the compiler generated; each is attributed back to the type that declares
/// it.
/// </para>
/// <para>
/// The scope is this module, and that is measurement rather than preference.
/// Writers elsewhere leave the field absent for reasons that have nothing to
/// do with a shared resource, so a solution-wide version of this rule would
/// need an exception list, and an exception list is the part that goes stale
/// while staying green. The module namespace draws the boundary by
/// construction instead.
/// </para>
/// </remarks>
public sealed class AuditApplicationInventoryTests
{
    /// <summary>
    /// The module whose effects this rule reads, named by a type so that
    /// moving the namespace moves the rule with it instead of quietly
    /// emptying it.
    /// </summary>
    private static readonly string ModuleNamespace = typeof(TemplateManagementModule).Namespace!;

    /// <summary>
    /// The feature family that owns the shared resource. Named by a type of
    /// the family for the same reason, and matched as a namespace prefix so a
    /// future subfolder joins the family instead of escaping it.
    /// </summary>
    private static readonly string LayoutNamespace = typeof(CreateLayout).Namespace!;

    /// <summary>
    /// The one member whose call is the whole question. Taken off the property
    /// rather than spelled as a name, so a rename of the field on the contract
    /// carries this rule along.
    /// </summary>
    private static readonly MethodInfo ApplicationSetter =
        typeof(AuditEntry).GetProperty(nameof(AuditEntry.Application))!.SetMethod!;

    private const BindingFlags AllDeclared =
        BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly;

    private const byte MultiByteOpCodePrefix = 0xFE;

    private static readonly OpCode?[] SingleByteOpCodes = BuildOpCodeTable(size: 1);

    private static readonly OpCode?[] TwoByteOpCodes = BuildOpCodeTable(size: 2);

    /// <summary>
    /// The walk has to be shown to reach its subject before either direction
    /// below carries any weight, because both are satisfied by an empty set
    /// and the cheapest way to reach one is an anchor that no longer points
    /// anywhere. The source tree is the independent oracle here: it answers
    /// from file text and never from the metadata the walk reads.
    /// </summary>
    [Fact]
    public void The_walk_over_compiled_code_reaches_every_file_that_builds_an_audit_entry()
    {
        var fromMetadata = Scan().Writers.Keys.Order(StringComparer.Ordinal).ToArray();
        var fromSource = FilesThatBuildAnEntry().Order(StringComparer.Ordinal).ToArray();

        fromSource.ShouldNotBeEmpty(
            "No file of this module was found building an audit entry. The source anchor no "
            + "longer points at the module, and until that is fixed this rule proves nothing.");

        var lostByTheWalk = fromSource
            .Except(fromMetadata, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        lostByTheWalk.ShouldBeEmpty(
            "These files build an audit entry and the walk over compiled code did not see it, "
            + "so whatever the walk reports about them is silence and not a verdict: "
            + string.Join(", ", lostByTheWalk));

        var unknownToTheSource = fromMetadata
            .Except(fromSource, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        unknownToTheSource.ShouldBeEmpty(
            "The walk found these building an audit entry and no file of the module does, so "
            + "the two sides are not looking at the same code: "
            + string.Join(", ", unknownToTheSource));

        // Set equality above tolerates a repeated key; the counts do not, and a
        // repeat is the one shape that would let an effect borrow the answer of
        // another.
        fromMetadata.Length.ShouldBe(fromSource.Length);
    }

    [Fact]
    public void No_layout_effect_names_an_application_on_the_row_it_writes()
    {
        AuditScan scan = Scan();
        AssertBothFamiliesWereFound(scan);

        var naming = LayoutWriters(scan)
            .Where(scan.Assigners.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();
        naming.ShouldBeEmpty(
            "A layout is pinned by the templates of many applications and the audit row holds "
            + "one application, so an effect on a layout has none to name and picking one is a "
            + "row that reads complete and states something nobody can support. These layout "
            + "effects now name one: " + string.Join(", ", naming));
    }

    [Fact]
    public void Every_other_audit_writer_of_the_module_names_an_application()
    {
        AuditScan scan = Scan();
        AssertBothFamiliesWereFound(scan);

        var silent = OtherWriters(scan)
            .Where(key => !scan.Assigners.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        silent.ShouldBeEmpty(
            "These effects act on a resource that belongs to one application and write a row "
            + "that does not name it. The absence of the field stops meaning what it means on a "
            + "layout row, which is that there is nothing to name, and starts reading as an "
            + "effect nobody attributed: " + string.Join(", ", silent));
    }

    /// <summary>
    /// Both families have to be found before the two directions above compare
    /// anything, and an assigner that builds no entry has to be absent, because
    /// it would mean the walk is attributing a call to a type that never makes
    /// the row.
    /// </summary>
    private static void AssertBothFamiliesWereFound(AuditScan scan)
    {
        LayoutWriters(scan).ShouldNotBeEmpty(
            "No effect on the shared resource was found writing an audit row. The family anchor "
            + "moved, and a rule that lost this half passes over an empty set.");

        OtherWriters(scan).ShouldNotBeEmpty(
            "Every audit writer found belongs to the shared-resource family, so there is nothing "
            + "left to hold the other half of the split against.");

        var assignersThatBuildNothing = scan.Assigners
            .Except(scan.Writers.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        assignersThatBuildNothing.ShouldBeEmpty(
            "These types name an application on an entry they do not build, which the walk "
            + "cannot attribute to any effect: " + string.Join(", ", assignersThatBuildNothing));
    }

    private static string[] LayoutWriters(AuditScan scan)
        => [.. scan.Writers.Where(pair => IsLayoutFamily(pair.Value)).Select(pair => pair.Key)];

    private static string[] OtherWriters(AuditScan scan)
        => [.. scan.Writers.Where(pair => !IsLayoutFamily(pair.Value)).Select(pair => pair.Key)];

    private static readonly string ModuleNamespacePrefix = ModuleNamespace + ".";

    private static readonly string LayoutNamespacePrefix = LayoutNamespace + ".";

    private const string ModulesRoot = "src/Platform.Api/Modules";

    private static bool IsLayoutFamily(Type effect)
        => effect.Namespace is string space
            && (string.Equals(space, LayoutNamespace, StringComparison.Ordinal)
                || space.StartsWith(LayoutNamespacePrefix, StringComparison.Ordinal));

    /// <summary>
    /// One pass over the module: which types build an audit entry and which
    /// name an application on one. Both are attributed to the type that
    /// declares the code, so a construction the compiler moved into a state
    /// machine still answers for the effect it belongs to.
    /// </summary>
    private static AuditScan Scan()
    {
        var writers = new Dictionary<string, Type>(StringComparer.Ordinal);
        var assigners = new HashSet<string>(StringComparer.Ordinal);

        foreach (Type type in ModuleTypes())
        {
            Type effect = EffectType(type);
            var key = EffectKey(effect);
            foreach (MethodBase method in BodiedMethods(type))
            {
                foreach (MethodBase target in CalledMembers(method))
                {
                    if (BuildsAnEntry(target))
                    {
                        writers[key] = effect;
                    }
                    else if (NamesAnApplication(target))
                    {
                        assigners.Add(key);
                    }
                }
            }
        }

        return new AuditScan(writers, assigners);
    }

    private sealed record AuditScan(Dictionary<string, Type> Writers, HashSet<string> Assigners);

    private static Type[] ModuleTypes()
        => [.. typeof(TemplateManagementModule).Assembly
            .GetTypes()
            .Where(type => type.Namespace is string space
                && (string.Equals(space, ModuleNamespace, StringComparison.Ordinal)
                    || space.StartsWith(ModuleNamespacePrefix, StringComparison.Ordinal)))];

    private static IEnumerable<MethodBase> BodiedMethods(Type type)
        => type.GetMethods(AllDeclared)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(AllDeclared))
            .Where(method => !method.IsAbstract);

    /// <summary>
    /// The members a method calls, decoded from its instruction stream. The
    /// operand length of every instruction comes from the operand kind the
    /// runtime itself publishes for the opcode, so the walk skips what it does
    /// not read instead of guessing, and an opcode it cannot name stops it
    /// loudly rather than sliding the cursor into the middle of an operand.
    /// </summary>
    private static IEnumerable<MethodBase> CalledMembers(MethodBase method)
    {
        MethodBody? body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } declaring
            ? declaring.GetGenericArguments()
            : null;
        Type[]? methodArguments = method.IsGenericMethodDefinition
            ? method.GetGenericArguments()
            : null;

        Module module = method.Module;
        var offset = 0;
        while (offset < il.Length)
        {
            OpCode instruction = ReadOpCode(il, ref offset);
            if (IsCallOrConstruction(instruction))
            {
                MethodBase? target = module.ResolveMethod(
                    BitConverter.ToInt32(il, offset),
                    typeArguments,
                    methodArguments);
                if (target is not null)
                {
                    yield return target;
                }
            }

            offset += OperandLength(instruction, il, offset);
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        var code = il[offset++];
        OpCode? instruction = code == MultiByteOpCodePrefix
            ? TwoByteOpCodes[il[offset++]]
            : SingleByteOpCodes[code];

        return instruction ?? throw new NotSupportedException(
            $"The instruction stream carries an opcode this walk cannot name at offset {offset}.");
    }

    private static bool IsCallOrConstruction(OpCode instruction)
        => instruction == OpCodes.Call
            || instruction == OpCodes.Callvirt
            || instruction == OpCodes.Newobj;

    private static int OperandLength(OpCode instruction, byte[] il, int offset)
        => instruction.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget
                or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget
                or OperandType.InlineField
                or OperandType.InlineI
                or OperandType.InlineMethod
                or OperandType.InlineSig
                or OperandType.InlineString
                or OperandType.InlineTok
                or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, offset)),
            _ => throw new NotSupportedException(
                $"The walk has no operand length for '{instruction.OperandType}'."),
        };

    private static OpCode?[] BuildOpCodeTable(int size)
    {
        var table = new OpCode?[256];
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode instruction && instruction.Size == size)
            {
                table[(ushort)instruction.Value & 0xFF] = instruction;
            }
        }

        return table;
    }

    private static bool BuildsAnEntry(MethodBase target)
        => target is ConstructorInfo && target.DeclaringType == typeof(AuditEntry);

    private static bool NamesAnApplication(MethodBase target)
        => target.Module == ApplicationSetter.Module
            && target.MetadataToken == ApplicationSetter.MetadataToken;

    /// <summary>
    /// The type an effect is written as, reached by stepping out of whatever
    /// the compiler generated around it. A state machine and a lambda closure
    /// both answer for the type that declares them.
    /// </summary>
    private static Type EffectType(Type type)
    {
        Type effect = type;
        while (effect.DeclaringType is Type declaring
            && effect.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            effect = declaring;
        }

        return effect;
    }

    private static string EffectKey(Type effect)
        => effect.FullName![(effect.Namespace!.Length + 1)..].Replace('+', '.');

    /// <summary>
    /// The files of the module that build an audit entry, which is the oracle
    /// the walk is held against. It reads text and knows nothing about
    /// metadata, so the two sides can only agree by both reaching the code.
    /// </summary>
    private static HashSet<string> FilesThatBuildAnEntry()
    {
        var needle = "new " + nameof(AuditEntry);

        return [.. Directory
            .EnumerateFiles(ModuleDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(needle, StringComparison.Ordinal))
            .Select(path => Path.GetFileNameWithoutExtension(path)!)];
    }

    private static string ModuleDirectory()
    {
        var directory = Path.Combine(
            FindSolutionRoot(),
            ModulesRoot.Replace('/', Path.DirectorySeparatorChar),
            ModuleNamespace.Split('.')[^1]);

        return Directory.Exists(directory)
            ? directory
            : throw new DirectoryNotFoundException(
                $"The module source anchor '{directory}' no longer exists.");
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
}
