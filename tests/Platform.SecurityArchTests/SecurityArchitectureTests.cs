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
    /// The two places this module builds an audit details document: a file
    /// that assigns <c>DetailsJson</c>, and the shared producer, named for
    /// what it produces so a rule finds it without following a call.
    /// </summary>
    private static string[] AuditDetailsProducers()
    {
        var moduleRoot = Path.Combine("Modules", "TemplateManagement");
        return [.. SourceFiles()
            .Where(path => path.Contains(moduleRoot, StringComparison.OrdinalIgnoreCase))
            .Where(path => path.EndsWith("AuditDetails.cs", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("DetailsJson", StringComparison.Ordinal))];
    }

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
}
