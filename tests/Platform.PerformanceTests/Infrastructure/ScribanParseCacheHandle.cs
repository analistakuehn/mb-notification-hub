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
/// code nobody runs. Every member the measured loop touches is bound once into
/// a delegate, so the arm pays a delegate call per operation and no reflection
/// at all. The parsed template comes back as an object because naming its type
/// would pull the engine's own package into this project for a value the arm
/// discards.
/// </remarks>
internal sealed class ScribanParseCacheHandle : IDisposable
{
    private const string TypeName =
        "NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating.ScribanParseCache";

    private readonly IDisposable _instance;
    private readonly Func<string, object> _getOrParse;
    private readonly MethodInfo _keep;
    private readonly Func<long> _hits;
    private readonly Func<long> _parses;
    private readonly Func<long> _residentBytes;
    private readonly Func<long> _residentEntries;

    private ScribanParseCacheHandle(IDisposable instance, Bindings bindings, long budget)
    {
        _instance = instance;
        _getOrParse = bindings.GetOrParse;
        _keep = bindings.Keep;
        _hits = bindings.Hits;
        _parses = bindings.Parses;
        _residentBytes = bindings.ResidentBytes;
        _residentEntries = bindings.ResidentEntries;
        Budget = budget;
    }

    /// <summary>Managed bytes the memoization is allowed to hold.</summary>
    internal long Budget { get; }

    internal long Hits => _hits();

    internal long Parses => _parses();

    internal long ResidentBytes => _residentBytes();

    internal long ResidentEntries => _residentEntries();

    internal static ScribanParseCacheHandle Create()
    {
        Type type = typeof(TemplateManagementDbContext).Assembly.GetType(TypeName, throwOnError: true)!;
        var instance = (IDisposable)Activator.CreateInstance(type)!;
        var budget = (long)Constant(type, "MaxResidentBytes");

        return new ScribanParseCacheHandle(
            instance,
            new Bindings(
                Bound(type, "GetOrParse").CreateDelegate<Func<string, object>>(instance),
                Bound(type, "Keep"),
                Getter<long>(type, "Hits", instance),
                Getter<long>(type, "Parses", instance),
                Getter<long>(type, "ResidentBytes", instance),
                Getter<long>(type, "ResidentEntries", instance)),
            budget);
    }

    internal object GetOrParse(string source) => _getOrParse(source);

    /// <summary>
    /// Loads one source the way a published render does: ask, then hand the
    /// parse back for the store to keep. It goes through reflection on every
    /// call instead of through a bound delegate, because the parsed tree is
    /// typed by the engine's own package and a delegate cannot be bound with a
    /// looser parameter type. Only the loading phase calls it, so no measured
    /// operation pays for it.
    /// </summary>
    internal void Load(string source) => _keep.Invoke(_instance, [source, _getOrParse(source)]);

    public void Dispose() => _instance.Dispose();

    private static object Constant(Type type, string name)
        => (type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw Missing(name)).GetRawConstantValue()!;

    private static MethodInfo Bound(Type type, string name)
        => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw Missing(name);

    private static Func<T> Getter<T>(Type type, string name, object instance)
        => (type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetGetMethod(nonPublic: true)
            ?? throw Missing(name))
            .CreateDelegate<Func<T>>(instance);

    private static InvalidOperationException Missing(string name)
        => new($"A memoização de parse não expõe '{name}'; a sonda está fora de sincronia com o tipo.");

    /// <summary>
    /// Everything the handle binds, in one value: seven of them loose would put
    /// the constructor past the parameter limit and make the call site a row of
    /// unnamed delegates.
    /// </summary>
    private sealed record Bindings(
        Func<string, object> GetOrParse,
        MethodInfo Keep,
        Func<long> Hits,
        Func<long> Parses,
        Func<long> ResidentBytes,
        Func<long> ResidentEntries);
}
