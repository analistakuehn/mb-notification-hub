using System.Reflection;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Composition;

namespace NotificationHub.ArchTests;

/// <summary>
/// Whether a slice logs is answered by one artifact, the handler's own
/// dependency list, and this rule only holds the two halves of that answer
/// together. A handler that takes a logger declares its events in the
/// companion file beside it, so a reader finds the vocabulary where the slice
/// is read instead of at the far end of a call chain; a companion file beside
/// a handler that takes no logger declares events nothing can emit and tells
/// that reader the slice logs when it does not.
/// </summary>
/// <remarks>
/// Neither direction claims a slice ought to log. That call belongs to the
/// slice and is deliberately left unenforced here: a rule that guessed it from
/// the verb, the folder, or the effect would be true today by the shape of the
/// current modules and false on the first slice that broke the coincidence.
/// </remarks>
public sealed class SliceLoggerCompanionTests
{
    /// <summary>
    /// The whole anchor of the discovery: the module source tree plus the file
    /// suffix of a handler. It names no feature folder on purpose, because
    /// those are renamed as a module grows and a rule anchored on one of them
    /// keeps passing over an empty set instead of reporting that it lost its
    /// subject.
    /// </summary>
    private const string ModulesRoot = "src/Platform.Api/Modules";

    private const string HandlerSuffix = ".Handler.cs";

    private const string CompanionSuffix = ".Handler.Logger.cs";

    private const string ModuleNamespaceRoot = "NotificationHub.Api.Modules.";

    /// <summary>
    /// The discovery has to be shown to reach its subject before either
    /// direction below carries any weight, because both are satisfied by an
    /// empty set and a stale anchor would turn the whole rule green. The
    /// compiled assembly is the independent oracle here: it answers from
    /// metadata and never from the path this rule walks.
    /// </summary>
    [Fact]
    public void Handler_discovery_reaches_every_compiled_handler()
    {
        var fromMetadata = HandlerTypes()
            .Select(SliceKey)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var fromSource = HandlerFiles()
            .Select(SliceKey)
            .Order(StringComparer.Ordinal)
            .ToArray();

        fromMetadata.ShouldNotBeEmpty();

        var lostByTheWalk = fromMetadata
            .Except(fromSource, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        lostByTheWalk.ShouldBeEmpty();

        var unknownToTheAssembly = fromSource
            .Except(fromMetadata, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        unknownToTheAssembly.ShouldBeEmpty();

        // Set equality above tolerates a repeated key; the counts do not, and a
        // repeat is the one shape that would let a slice borrow another
        // slice's answer.
        fromSource.Length.ShouldBe(fromMetadata.Length);
    }

    [Fact]
    public void Handler_that_takes_a_logger_declares_its_events_beside_itself()
    {
        HashSet<string> logging = LoggingSlices();

        var missing = HandlerFiles()
            .Where(path => logging.Contains(SliceKey(path)))
            .Where(path => !File.Exists(CompanionPath(path)))
            .Select(path => Relative(CompanionPath(path)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void Companion_file_sits_only_beside_a_handler_that_takes_a_logger()
    {
        HashSet<string> logging = LoggingSlices();

        var stranded = CompanionFiles()
            .Where(path => !logging.Contains(SliceKey(path)))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        stranded.ShouldBeEmpty();
    }

    /// <summary>
    /// The slices whose handler takes a logger, read from the compiled
    /// constructors rather than from the text of the file, so a mention inside
    /// a comment or a reformatted parameter list cannot change the answer.
    /// </summary>
    private static HashSet<string> LoggingSlices()
        => [.. HandlerTypes().Where(TakesLogger).Select(SliceKey)];

    private static Type[] HandlerTypes()
        => [.. SolutionAssemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Name == "Handler"
                && type.Namespace is not null
                && type.Namespace.StartsWith(ModuleNamespaceRoot, StringComparison.Ordinal))];

    private static bool TakesLogger(Type handler)
        => handler
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType.IsGenericType
                && parameter.ParameterType.GetGenericTypeDefinition() == typeof(ILogger<>));

    /// <summary>
    /// Owning module plus use case, the pair that identifies a slice on both
    /// sides of the rule. The module qualifies the use case so two modules that
    /// happen to name one the same never answer for each other.
    /// </summary>
    private static string SliceKey(Type handler)
        => $"{handler.Namespace![ModuleNamespaceRoot.Length..].Split('.')[0]}"
            + $".{handler.DeclaringType?.Name ?? handler.Name}";

    private static string SliceKey(string path)
    {
        var name = Path.GetFileName(path);
        var useCase = name.EndsWith(CompanionSuffix, StringComparison.Ordinal)
            ? name[..^CompanionSuffix.Length]
            : name[..^HandlerSuffix.Length];

        return $"{ModuleOf(path)}.{useCase}";
    }

    private static string ModuleOf(string path)
        => Path.GetRelativePath(ModulesDirectory(), path)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)[0];

    private static string CompanionPath(string handlerPath)
        => handlerPath[..^HandlerSuffix.Length] + CompanionSuffix;

    private static string[] HandlerFiles() => ModuleSourceFiles(HandlerSuffix);

    private static string[] CompanionFiles() => ModuleSourceFiles(CompanionSuffix);

    private static string[] ModuleSourceFiles(string suffix)
        => [.. Directory
            .EnumerateFiles(ModulesDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(suffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    private static string Relative(string path)
        => Path.GetRelativePath(FindSolutionRoot(), path);

    private static string ModulesDirectory()
    {
        var directory = Path.Combine(
            FindSolutionRoot(),
            ModulesRoot.Replace('/', Path.DirectorySeparatorChar));

        return Directory.Exists(directory)
            ? directory
            : throw new DirectoryNotFoundException(
                $"The module source anchor '{ModulesRoot}' no longer exists.");
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
