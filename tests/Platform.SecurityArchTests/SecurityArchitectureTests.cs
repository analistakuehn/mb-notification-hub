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
        var moduleRoot = Path.Combine("Modules", "TemplateManagement");
        var producers = SourceFiles()
            .Where(path => path.Contains(moduleRoot, StringComparison.OrdinalIgnoreCase))
            .Where(path => path.EndsWith("AuditDetails.cs", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("DetailsJson", StringComparison.Ordinal))
            .ToArray();

        // A path that stops matching would turn the rule into a green that
        // scanned nothing at all.
        producers.ShouldNotBeEmpty();

        var findings = producers
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(item => ValidationCheckText().IsMatch(item.line))
            .Select(item => $"{item.path}:{item.number}")
            .ToArray();

        findings.ShouldBeEmpty();
    }

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

    [GeneratedRegex(@"\b\w*[Cc]heck\w*\.(?:Message|Location)\b")]
    private static partial Regex ValidationCheckText();
}
