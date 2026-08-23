using System.Collections.Concurrent;
using Scriban;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

/// <summary>
/// Memoization of parsed Scriban templates keyed by the exact source text.
/// Published content is immutable per version, so the same source always
/// yields the same AST and the entries never expire; rendering with a fresh
/// context per call keeps the shared AST safe. The cache is bounded: when
/// drafts and previews push it past the ceiling it resets whole, because the
/// hot published sources repopulate on first use.
/// </summary>
internal sealed class ScribanParseCache
{
    internal const int MaxEntries = 1024;

    private readonly ConcurrentDictionary<string, Template> _templates = new(StringComparer.Ordinal);
    private long _hits;
    private long _parses;

    /// <summary>How many lookups were answered without parsing. Observability for tests.</summary>
    internal long Hits => Interlocked.Read(ref _hits);

    /// <summary>How many lookups had to parse. Observability for tests.</summary>
    internal long Parses => Interlocked.Read(ref _parses);

    /// <summary>
    /// The parsed template for the source, from the cache or from a fresh
    /// parse. A template that parses with errors is returned but never
    /// cached, so the error path stays visible on every call.
    /// </summary>
    internal Template GetOrParse(string source)
    {
        if (_templates.TryGetValue(source, out Template? cached))
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        Interlocked.Increment(ref _parses);
        var parsed = Template.Parse(source);
        if (!parsed.HasErrors)
        {
            if (_templates.Count >= MaxEntries)
            {
                _templates.Clear();
            }

            _templates.TryAdd(source, parsed);
        }

        return parsed;
    }
}
