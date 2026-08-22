using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Composition;
using NotificationHub.SharedKernel;

namespace NotificationHub.SecurityArchTests;

public sealed partial class SecurityArchitectureTests
{
    private static readonly Assembly[] Production = SolutionAssemblies.All;

    private static readonly string[] ProductionRoots =
    [
        "src/Platform.Api",
    ];

    [Fact]
    public void Source_must_not_use_known_dangerous_apis()
    {
        string[] findings = SourceFiles()
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(item => DangerousApi().IsMatch(item.line))
            .Select(item => $"{item.path}:{item.number}")
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void State_changing_endpoints_must_declare_authorization_and_rate_limiting()
    {
        string[] findings = SourceFiles()
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
        string[] domainTypeNames = Production
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Namespace is string value && DomainNamespace().IsMatch(value))
            .Select(type => type.Name.Split('`')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] findings = SourceFiles()
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
        string[] personalNames = Production
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

        string[] messages = Production
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(method => method.GetCustomAttribute<LoggerMessageAttribute>()?.Message)
            .OfType<string>()
            .ToArray();

        string[] findings = personalNames
            .Where(name => messages.Any(message =>
                message.Contains("{" + name + "}", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void Security_paths_must_not_use_pseudo_random_generators()
    {
        string[] findings = SourceFiles()
            .Where(path => SecurityPath().IsMatch(path))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(item => PseudoRandom().IsMatch(item.line))
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
}
