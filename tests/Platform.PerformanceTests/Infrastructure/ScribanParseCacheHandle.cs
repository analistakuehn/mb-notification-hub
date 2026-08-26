using System.Reflection;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;

namespace NotificationHub.PerformanceTests.Infrastructure;

/// <summary>
/// Drives the parse memoization of TemplateManagement, which is internal to the
/// API assembly and not visible to this project.
/// </summary>
/// <remarks>
/// Binding it by name is the price of measuring the deployed type instead of a
/// copy of it: a probe that rebuilt the same budget locally would report on
/// code nobody runs. Every member is bound once into a delegate, so the arm
/// pays a delegate call per operation and no reflection at all. The parsed
/// template comes back as an object because naming its type would pull the
/// engine's own package into this project for a value the arm discards.
/// </remarks>
internal sealed class ScribanParseCacheHandle : IDisposable
{
    private const string TypeName =
        "NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating.ScribanParseCache";

    private readonly IDisposable _instance;
    private readonly Func<string, object> _getOrParse;
    private readonly Func<long> _hits;
    private readonly Func<long> _parses;
    private readonly Func<long> _residentChars;

    private ScribanParseCacheHandle(
        IDisposable instance,
        Func<string, object> getOrParse,
        Func<long> hits,
        Func<long> parses,
        Func<long> residentChars,
        long budget)
    {
        _instance = instance;
        _getOrParse = getOrParse;
        _hits = hits;
        _parses = parses;
        _residentChars = residentChars;
        Budget = budget;
    }

    /// <summary>Source characters the memoization is allowed to hold.</summary>
    internal long Budget { get; }

    internal long Hits => _hits();

    internal long Parses => _parses();

    internal long ResidentChars => _residentChars();

    internal static ScribanParseCacheHandle Create()
    {
        Type type = typeof(TemplateManagementDbContext).Assembly.GetType(TypeName, throwOnError: true)!;
        var instance = (IDisposable)Activator.CreateInstance(type)!;
        var budget = (int)type
            .GetField("MaxSourceChars", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;

        return new ScribanParseCacheHandle(
            instance,
            Bound(type, "GetOrParse").CreateDelegate<Func<string, object>>(instance),
            Getter<long>(type, "Hits", instance),
            Getter<long>(type, "Parses", instance),
            Getter<long>(type, "ResidentChars", instance),
            budget);
    }

    internal object GetOrParse(string source) => _getOrParse(source);

    public void Dispose() => _instance.Dispose();

    private static MethodInfo Bound(Type type, string name)
        => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"A memoização de parse não expõe '{name}'; a sonda está fora de sincronia com o tipo.");

    private static Func<T> Getter<T>(Type type, string name, object instance)
        => (type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetGetMethod(nonPublic: true)
            ?? throw new InvalidOperationException(
                $"A memoização de parse não expõe '{name}'; a sonda está fora de sincronia com o tipo."))
            .CreateDelegate<Func<T>>(instance);
}
