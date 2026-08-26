using System.Reflection;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;

namespace NotificationHub.PerformanceTests.Contention;

/// <summary>
/// Drives the published-read memoization of TemplateManagement, which is
/// internal to the API assembly and not visible to this project.
/// </summary>
/// <remarks>
/// Binding it by name is the price of measuring the deployed type instead of a
/// copy of it: a probe that rebuilt the same configuration locally would report
/// on code nobody runs. Every member is bound once into a delegate, so the arm
/// pays a delegate call per operation and no reflection at all. Granting this
/// project internals visibility would replace the whole file with a using.
/// </remarks>
internal sealed class PublishedReadCacheHandle : IDisposable
{
    private const string TypeName =
        "NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration.PublishedReadCache";

    private readonly IDisposable _instance;
    private readonly Action<string, string> _setPointer;
    private readonly TryGetPointerDelegate _tryGetPointer;
    private readonly Func<int> _pointerCount;
    private readonly Func<long> _pointerHits;
    private readonly Func<long> _pointerLoads;

    internal delegate bool TryGetPointerDelegate(string key, out string value);

    private PublishedReadCacheHandle(
        IDisposable instance,
        Action<string, string> setPointer,
        TryGetPointerDelegate tryGetPointer,
        Func<int> pointerCount,
        Func<long> pointerHits,
        Func<long> pointerLoads,
        int ceiling)
    {
        _instance = instance;
        _setPointer = setPointer;
        _tryGetPointer = tryGetPointer;
        _pointerCount = pointerCount;
        _pointerHits = pointerHits;
        _pointerLoads = pointerLoads;
        Ceiling = ceiling;
    }

    /// <summary>Entries the pointer family is allowed to hold.</summary>
    internal int Ceiling { get; }

    internal int PointerCount => _pointerCount();

    internal long PointerHits => _pointerHits();

    internal long PointerLoads => _pointerLoads();

    internal static PublishedReadCacheHandle Create()
    {
        Type type = typeof(TemplateManagementDbContext).Assembly.GetType(TypeName, throwOnError: true)!;
        var instance = (IDisposable)Activator.CreateInstance(type, TimeProvider.System)!;
        MethodInfo set = Bound(type, "SetPointer").MakeGenericMethod(typeof(string));
        MethodInfo get = Bound(type, "TryGetPointer").MakeGenericMethod(typeof(string));
        var ceiling = (int)type
            .GetField("MaxEntries", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;

        return new PublishedReadCacheHandle(
            instance,
            set.CreateDelegate<Action<string, string>>(instance),
            get.CreateDelegate<TryGetPointerDelegate>(instance),
            Getter<int>(type, "PointerCount", instance),
            Getter<long>(type, "PointerHits", instance),
            Getter<long>(type, "PointerLoads", instance),
            ceiling);
    }

    internal bool TryGetPointer(string key, out string value) => _tryGetPointer(key, out value);

    internal void SetPointer(string key, string value) => _setPointer(key, value);

    public void Dispose() => _instance.Dispose();

    private static MethodInfo Bound(Type type, string name)
        => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"A memoização publicada não expõe '{name}'; a sonda está fora de sincronia com o tipo.");

    private static Func<T> Getter<T>(Type type, string name, object instance)
        => (type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetGetMethod(nonPublic: true)
            ?? throw new InvalidOperationException(
                $"A memoização publicada não expõe '{name}'; a sonda está fora de sincronia com o tipo."))
            .CreateDelegate<Func<T>>(instance);
}
