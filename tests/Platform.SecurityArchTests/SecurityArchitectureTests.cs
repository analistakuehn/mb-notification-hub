using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Composition;
using NotificationHub.SharedKernel;
using NotificationHub.Worker;

namespace NotificationHub.SecurityArchTests;

public sealed partial class SecurityArchitectureTests
{
    // Both hosts are production: the API assemblies plus the worker host,
    // composed here because a host-to-host project reference would be
    // circular. Every rule below scans the same set.
    private static readonly Assembly[] Production =
        [.. SolutionAssemblies.All, typeof(AssemblyMarker).Assembly];

    private static readonly string[] ProductionRoots =
    [
        "src/Platform.Api",
        "src/Platform.Worker",
    ];

    /// <summary>
    /// The one file the render paths delegate final content to. It is named
    /// here rather than matched by a pattern: the rules below claim that the
    /// work happens in a single known place, and a pattern would let a second
    /// file answer for it.
    /// </summary>
    private const string OutputPolicyFileName = "RenderedOutputPolicy.cs";

    [Fact]
    public void Source_must_not_use_known_dangerous_apis()
    {
        var findings = SourceFiles()
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(item => DangerousApi().IsMatch(item.line))
            .Select(item => $"{item.path}:{item.number}")
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void State_changing_endpoints_must_declare_authorization_and_rate_limiting()
    {
        var findings = SourceFiles()
            .SelectMany(path => Regex.Split(File.ReadAllText(path), @";\s*")
                .Where(statement => StateChangingEndpoint().IsMatch(statement))
                .Where(statement => !statement.Contains("RequireAuthorization", StringComparison.Ordinal)
                    || !statement.Contains("RequireRateLimiting", StringComparison.Ordinal))
                .Select(_ => path))
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void Endpoint_inputs_must_not_bind_domain_types()
    {
        var domainTypeNames = Production
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Namespace is string value && DomainNamespace().IsMatch(value))
            .Select(type => type.Name.Split('`')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var findings = SourceFiles()
            .SelectMany(path => Regex.Split(File.ReadAllText(path), @";\s*")
                .Where(statement => EndpointRegistration().IsMatch(statement))
                .Where(statement => domainTypeNames.Any(name =>
                    Regex.IsMatch(statement, $@"\b{Regex.Escape(name)}\b")))
                .Select(_ => path))
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void Personal_data_names_must_not_appear_in_logger_templates()
    {
        var personalNames = Production
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .Where(property => property.IsDefined(typeof(PersonalDataAttribute), inherit: true))
            .Select(property => property.Name)
            .Concat(Production
                .SelectMany(assembly => assembly.GetTypes())
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                .SelectMany(method => method.GetParameters())
                .Where(parameter => parameter.IsDefined(typeof(PersonalDataAttribute), inherit: true))
                .Select(parameter => parameter.Name)
                .OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var messages = Production
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(method => method.GetCustomAttribute<LoggerMessageAttribute>()?.Message)
            .OfType<string>()
            .ToArray();

        var findings = personalNames
            .Where(name => messages.Any(message =>
                message.Contains("{" + name + "}", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void Security_paths_must_not_use_pseudo_random_generators()
    {
        var findings = SourceFiles()
            .Where(path => SecurityPath().IsMatch(path))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(item => PseudoRandom().IsMatch(item.line))
            .Select(item => $"{item.path}:{item.number}")
            .ToArray();

        findings.ShouldBeEmpty();
    }

    /// <summary>
    /// The audit trail carries evidence a reader can act on, and the message
    /// and the location of a validation check are not that: the message
    /// interpolates a name lifted out of the content being published (a
    /// declared variable, a link host offered by a wrapper), and the location
    /// points at the exact content unit that produced the finding. The check
    /// name and the fact that it warned answer the audit question; the text
    /// around them only carries content into a row nobody can rewrite.
    /// <para>
    /// The scan covers the two places this module builds such a document: the
    /// handler that assigns <c>DetailsJson</c>, and the shared producer, whose
    /// file is named for what it produces so this rule can find it without
    /// following a call. A type-level rule cannot reach either one, because the
    /// field selection happens inside an anonymous type and leaves no member to
    /// inspect in the assembly.
    /// </para>
    /// </summary>
    [Fact]
    public void No_audit_details_of_the_template_module_serialize_a_validation_check_message()
    {
        var producers = AuditDetailsProducers();

        // A path that stops matching would turn the rule into a green that
        // scanned nothing at all.
        producers.ShouldNotBeEmpty();

        var findings = Findings(producers, ValidationCheckText());

        findings.ShouldBeEmpty();
    }

    /// <summary>
    /// The prose an operator types beside the reason code of a deprecate or a
    /// disable never reaches the trail. It is free text written while traffic
    /// is being stopped, so nothing bounds what lands in it, and the trail is
    /// append-only by trigger and hash-chained per partition: a national
    /// identifier or a token that arrives there stays for the retention of the
    /// trail, inside the bytes the chain covers. The details carry the
    /// reference to the stored note instead, and the note itself lives in a
    /// row that can be erased.
    /// <para>
    /// The rule is checkable only because the note reaches the handler as a
    /// type. As a bare string beside the reason it read exactly like the
    /// reason, and no scan could tell the two apart; carried as a type, the
    /// single way to reach the prose is to name <c>Text</c>, which is what
    /// this pattern looks for. It catches the local and the member alike and,
    /// like the rule above, it stops at the file boundary: prose laundered
    /// through a helper in another file is out of its reach.
    /// </para>
    /// </summary>
    [Fact]
    public void No_audit_details_of_the_template_module_serialize_the_text_of_a_lifecycle_note()
    {
        var producers = AuditDetailsProducers();

        producers.ShouldNotBeEmpty();

        var findings = Findings(producers, LifecycleNoteProse());

        findings.ShouldBeEmpty();
    }

    /// <summary>
    /// A link the catalog refuses cannot leave this module through a render
    /// path that simply never asked. The allowlist is checked while a version
    /// is authored, but authoring sees fragments: the destination a reader
    /// receives exists only after interpolation, after the layout has framed
    /// the body, and after the channel normalizer has rewritten it. A path that
    /// drives the sandbox to the end and hands the text back without consulting
    /// the output policy reproduces the defect this rule exists to stop:
    /// validation that is present and does not reach the result.
    /// <para>
    /// This rule reads the receiver of the sandbox call, which is the one thing
    /// a render cannot do without. It is the reach the sibling rule below lacks:
    /// a new orchestrator that composes its own final text without ever naming
    /// the wrapper type or the normalizer still has to render, and it is caught
    /// here.
    /// </para>
    /// <para>
    /// Known residue, measured and stated rather than left silent. The pattern
    /// anchors on the identifier <c>engine</c>, which is a parameter name and
    /// nothing stronger: a receiver renamed at its declaration escapes this
    /// rule. A composer that neither drives the sandbox nor names the wrapper
    /// or the normalizer escapes all three rules of this family.
    /// </para>
    /// <para>
    /// The method name is deliberately open-ended rather than a list of the
    /// entry points that exist today. A closed list is a rule that a rename
    /// silently empties: the call sites move, every file stops matching, and
    /// what the rule reports is that it found nothing to check. The emptiness
    /// tripwire above catches that, and matching any render entry point on the
    /// receiver keeps it from happening in the first place.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_file_that_renders_through_the_sandbox_applies_the_output_policy()
    {
        var renderers = SandboxRenderers();

        // A signature that stopped matching would turn the rule into a green
        // that scanned no render path at all.
        renderers.ShouldNotBeEmpty();

        var findings = FilesMissingTheOutputPolicy(renderers);

        findings.ShouldBeEmpty();
    }

    /// <summary>
    /// The same claim read from the producing side: whoever names the layout
    /// wrapper or the channel normalizer owns final content and consults the
    /// output policy over it.
    /// <para>
    /// A producer is recognized by the two transformations only final content
    /// receives in this module, and not by a call graph: the composition
    /// happens across private methods of one file, which leaves no type or
    /// member for an assembly-level rule to inspect. The scan therefore reaches
    /// exactly as far as the file, and it claims presence rather than order,
    /// because text has no order of execution to read. That the guard runs
    /// after normalization and before the hash is pinned by the behavior tests,
    /// not here.
    /// </para>
    /// <para>
    /// The count is asserted because presence alone goes green over a shrinking
    /// set: three files carry final content today, the two orchestrators and
    /// the policy itself, and a change that empties one of them without moving
    /// its work has to say so here.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_producer_of_final_rendered_content_consults_the_output_policy()
    {
        var producers = RenderedContentProducers();

        // A signature that stopped matching would turn the rule into a green
        // that scanned no render path at all.
        producers.Length.ShouldBeGreaterThanOrEqualTo(3);

        var findings = FilesMissingTheOutputPolicy(producers);

        findings.ShouldBeEmpty();
    }

    /// <summary>
    /// The files of a scanned set that never call the output policy. The single
    /// exemption is the policy's own file, named and not matched by a pattern,
    /// because it composes final content and cannot call itself.
    /// <para>
    /// The exemption opens no hole: that file does not leave this family, it
    /// answers to a stricter member of it. <see
    /// cref="The_output_policy_normalizes_bans_links_guards_destinations_measures_size_and_hashes"/>
    /// requires all five steps to be present in it, while these two rules only
    /// require a call to be present. Exempting it here and demanding more of it
    /// there is what lets the guard pattern stay a single alternative: accepting
    /// a direct call to the destination policy as a substitute would pass a
    /// third orchestrator that guards the destination and skips normalization,
    /// the ban, and the hash.
    /// </para>
    /// </summary>
    private static string[] FilesMissingTheOutputPolicy(IEnumerable<string> files)
        => [.. files
            .Where(path => Path.GetFileName(path) != OutputPolicyFileName)
            .Where(path => !RenderedOutputGuard().IsMatch(File.ReadAllText(path)))];

    /// <summary>
    /// The channel normalizer runs in one place. Two call sites are two
    /// orderings waiting to diverge, and the order is what the audited hash
    /// depends on: normalization has to precede the hash, so a second site that
    /// normalizes after hashing ships a trail describing a message nobody sent.
    /// Pinning the cardinality is what makes the two rules above point
    /// somewhere: they accept any file that names the policy, and this one
    /// states that the naming leads to a single implementation.
    /// </summary>
    [Fact]
    public void Exactly_one_file_normalizes_channel_content_and_it_is_the_output_policy()
    {
        var normalizers = ModuleSourceFiles()
            .Where(path => ChannelNormalization().IsMatch(File.ReadAllText(path)))
            .ToArray();

        normalizers.Length.ShouldBe(1);
        Path.GetFileName(normalizers[0]).ShouldBe(OutputPolicyFileName);
    }

    /// <summary>
    /// The file the three rules above point at does the five things the render
    /// path delegates to it, in one place: it normalizes for the channel, bans
    /// a link inside an authentication SMS, guards the destination, measures
    /// the result against what the channel carries, and hashes the text it
    /// returns. Without this, an output policy emptied of one step keeps every
    /// other rule of this family green while the step it lost stops running
    /// anywhere.
    /// </summary>
    [Fact]
    public void The_output_policy_normalizes_bans_links_guards_destinations_measures_size_and_hashes()
    {
        var files = ModuleSourceFiles()
            .Where(path => Path.GetFileName(path) == OutputPolicyFileName)
            .ToArray();

        files.Length.ShouldBe(1);

        var source = File.ReadAllText(files[0]);
        (string Step, Regex Pattern)[] steps =
        [
            ("channel normalization", ChannelNormalization()),
            ("authentication link ban", AuthenticationLinkBan()),
            ("destination guard", RenderedDestinationGuard()),
            ("rendered size ceiling", RenderedSizeCeiling()),
            ("canonical content hash", CanonicalContentHash()),
        ];

        var missing = steps
            .Where(step => !step.Pattern.IsMatch(source))
            .Select(step => step.Step)
            .ToArray();

        missing.ShouldBeEmpty();
    }

    /// <summary>
    /// The files of the template module that compose the content a provider or
    /// a caller receives, recognized by the transformations only that content
    /// gets: the channel normalizer and the layout wrapper.
    /// </summary>
    private static string[] RenderedContentProducers()
        => [.. ModuleSourceFiles().Where(path => FinalContentComposition().IsMatch(File.ReadAllText(path)))];

    /// <summary>
    /// The files of the template module that drive the sandbox to produce text,
    /// recognized by the call every render makes whatever it does afterwards.
    /// </summary>
    private static string[] SandboxRenderers()
        => [.. ModuleSourceFiles().Where(path => SandboxRender().IsMatch(File.ReadAllText(path)))];

    private static IEnumerable<string> ModuleSourceFiles()
    {
        var moduleRoot = Path.Combine("Modules", "TemplateManagement");
        return SourceFiles().Where(path => path.Contains(moduleRoot, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The two places this module builds an audit details document: a file
    /// that assigns <c>DetailsJson</c>, and the shared producer, named for
    /// what it produces so a rule finds it without following a call.
    /// </summary>
    private static string[] AuditDetailsProducers()
        => [.. ModuleSourceFiles()
            .Where(path => path.EndsWith("AuditDetails.cs", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("DetailsJson", StringComparison.Ordinal))];

    private static string[] Findings(IEnumerable<string> files, Regex pattern)
        => [.. files
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(item => pattern.IsMatch(item.line))
            .Select(item => $"{item.path}:{item.number}")];

    private static IEnumerable<string> SourceFiles()
        => ProductionRoots
            .Select(relative => Path.Combine(
                FindSolutionRoot(),
                relative.Replace('/', Path.DirectorySeparatorChar)))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !BuildOutput().IsMatch(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

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

    [GeneratedRegex(@"FromSqlRaw\s*\(\s*\$|ExecuteSqlRaw\s*\(\s*\$|BinaryFormatter|SoapFormatter|NetDataContractSerializer")]
    private static partial Regex DangerousApi();

    [GeneratedRegex(@"\bMap(Post|Put|Patch|Delete)\s*\(")]
    private static partial Regex StateChangingEndpoint();

    [GeneratedRegex(@"\bMap(Get|Post|Put|Patch|Delete|Methods)\s*\(")]
    private static partial Regex EndpointRegistration();

    [GeneratedRegex(@"^NotificationHub\.Api\.Modules\.[^.]+\.Domain(\.|$)")]
    private static partial Regex DomainNamespace();

    [GeneratedRegex(@"[\\/](Auth|Authentication|Token|Crypto|Cryptography)(?:[\\/]|[^\\/]*\.cs$)", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityPath();

    [GeneratedRegex(@"[\\/](bin|obj)[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex BuildOutput();

    [GeneratedRegex(@"\bRandom(?:\.Shared)?\b")]
    private static partial Regex PseudoRandom();

    [GeneratedRegex(@"\b\w*[Cc]heck\w*\??\.(?:Message|Location)\b")]
    private static partial Regex ValidationCheckText();

    [GeneratedRegex(@"\b\w*[Nn]ote\w*\??\.Text\b")]
    private static partial Regex LifecycleNoteProse();

    [GeneratedRegex(@"\bengine\s*\.\s*Render\w*Async\s*\(")]
    private static partial Regex SandboxRender();

    [GeneratedRegex(@"SmsContentNormalizer\s*\.\s*Normalize\s*\(|\bLayoutWrapper\b")]
    private static partial Regex FinalContentComposition();

    [GeneratedRegex(@"\bRenderedOutputPolicy\s*\.\s*Apply\s*\(")]
    private static partial Regex RenderedOutputGuard();

    [GeneratedRegex(@"SmsContentNormalizer\s*\.\s*Normalize\s*\(")]
    private static partial Regex ChannelNormalization();

    [GeneratedRegex(@"RenderedDestinationPolicy\s*\.\s*Validate\s*\(")]
    private static partial Regex RenderedDestinationGuard();

    [GeneratedRegex(@"TemplateValidation\s*\.\s*ContainsLinkLikeText\s*\(")]
    private static partial Regex AuthenticationLinkBan();

    [GeneratedRegex(@"SmsSegmentCeiling\s*\.\s*Admits\s*\(")]
    private static partial Regex RenderedSizeCeiling();

    [GeneratedRegex(@"CanonicalHash\s*\.\s*OfFields\s*\(")]
    private static partial Regex CanonicalContentHash();
}
