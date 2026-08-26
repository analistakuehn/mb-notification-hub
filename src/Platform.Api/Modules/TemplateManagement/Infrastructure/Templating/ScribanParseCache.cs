using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Scriban;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

/// <summary>
/// Memoization of parsed Scriban templates keyed by the exact source text.
/// Published content is immutable per version, so the same source always
/// yields the same AST and the entries never expire; rendering with a fresh
/// context per call keeps the shared AST safe.
/// </summary>
/// <remarks>
/// The budget is counted in source characters and never in entries, because
/// what one entry weighs is the author's choice: a source at the size limit
/// retains thousands of times what a subject line does, so a ceiling on the
/// count bounds nothing about memory. At the budget the store refuses the
/// arriving source and schedules a compaction that frees a slice of it, so a
/// burst of drafts and previews costs a slice of the hot set instead of all of
/// it, and the sources that survive keep answering while the slice is freed.
/// </remarks>
internal sealed class ScribanParseCache : IDisposable
{
    /// <summary>
    /// Source characters the memoization is allowed to hold. It has to clear
    /// the largest source one template may carry by a wide margin: a source
    /// that alone passed the budget would be refused on arrival and parsed
    /// again on every single call, without a word.
    /// </summary>
    internal const int MaxSourceChars = 1_048_576;

    /// <summary>Share of the budget one compaction pass frees once it is full.</summary>
    internal const double CompactionShare = 0.05;

    private readonly MemoryCache _templates = new(Options.Create(new MemoryCacheOptions
    {
        SizeLimit = MaxSourceChars,
        CompactionPercentage = CompactionShare,
    }));

    private long _hits;
    private long _parses;

    /// <summary>How many lookups were answered without parsing. Observability for tests.</summary>
    internal long Hits => Interlocked.Read(ref _hits);

    /// <summary>How many lookups had to parse. Observability for tests.</summary>
    internal long Parses => Interlocked.Read(ref _parses);

    /// <summary>
    /// Source characters resident right now. Reading it walks the whole store,
    /// so it stays out of the read path and exists for tests.
    /// </summary>
    internal long ResidentChars => _templates.Keys.Sum(key => (long)((string)key).Length);

    /// <summary>
    /// The parsed template for the source, from the cache or from a fresh
    /// parse. A template that parses with errors is returned but never
    /// cached, so the error path stays visible on every call.
    /// </summary>
    internal Template GetOrParse(string source)
    {
        if (_templates.TryGetValue(source, out var stored) && stored is Template cached)
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        Interlocked.Increment(ref _parses);
        var parsed = Template.Parse(source);
        if (!parsed.HasErrors)
        {
            // One entry description per miss, unshareable on purpose: an entry
            // weighs the length of its own source. Only the miss path allocates
            // it, and that path has just paid for a parse.
            //
            // Two callers that miss the same source at once each keep the AST
            // they parsed and the store keeps the last one to arrive. The AST is
            // read-only during a render, so which of the two stays resident
            // changes nothing for either caller.
            _templates.Set(source, parsed, new MemoryCacheEntryOptions { Size = source.Length });
        }

        return parsed;
    }

    public void Dispose() => _templates.Dispose();
}
