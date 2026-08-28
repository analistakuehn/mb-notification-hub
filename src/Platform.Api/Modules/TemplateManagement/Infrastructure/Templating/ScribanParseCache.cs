using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Scriban;
using Scriban.Syntax;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

/// <summary>
/// Memoization of parsed Scriban templates keyed by the exact source text.
/// Published content is immutable per version, so the same source always
/// yields the same AST and the entries never expire; rendering with a fresh
/// context per call keeps the shared AST safe.
/// </summary>
/// <remarks>
/// The budget is counted in bytes of retained memory, never in entries and
/// never in source characters, because neither says what an entry weighs: the
/// same thousand characters buy one text node or fifteen hundred expression
/// nodes, and the parsed tree of the second outweighs the first by two orders
/// of magnitude. What the budget has to bound is the process, and bytes is the
/// only unit that bounds it.
/// <para>
/// A source is only kept after a render finished over it, and only when the
/// caller declared the source immutable. Everything else is parsed and dropped.
/// </para>
/// </remarks>
internal sealed class ScribanParseCache : IDisposable
{
    /// <summary>
    /// Managed bytes the memoization is allowed to hold. It is a share of the
    /// process, so it is stated as memory and not as an amount of content.
    /// </summary>
    internal const long MaxResidentBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Share of the store one compaction pass drops when it runs on its own.
    /// It is no longer what decides admission, which is sized by the arriving
    /// entry, and it stays as the backstop for a refusal this type lost a race
    /// to.
    /// </summary>
    internal const double CompactionShare = 0.05;

    /// <summary>
    /// What one source character costs: a UTF-16 character of the very string
    /// the parsed tree keeps alive through its slices.
    /// </summary>
    internal const int BytesPerSourceChar = 2;

    /// <summary>
    /// What one parsed node costs. Measured on 2026-08-27 against Scriban 7.2.6
    /// on .NET 10 x64, over the shapes this module renders and up to a source at
    /// the size ceiling: 152 to 170 bytes per node once the tree is large enough
    /// that the fixed cost of a template stops dominating, and down to 119 for
    /// a source of pure text, whose handful of nodes carries that fixed cost
    /// alone. It is a reading of that engine on that runtime, not a property of
    /// the grammar, and an engine upgrade is expected to move it.
    /// </summary>
    internal const int BytesPerNode = 160;

    /// <summary>
    /// What the store spends per entry on its own bookkeeping, measured the
    /// same way: the cache entry, its node in the map and the key reference.
    /// The key costs nothing beyond that, being the very string the tree
    /// already holds.
    /// </summary>
    internal const int EntryOverheadBytes = 298;

    /// <summary>
    /// Densest tree one source character can buy, measured over the shapes the
    /// parser accepts: a chain of unary operators reaches 1.99 nodes per
    /// character and a chain of member accesses 1.50, while the content this
    /// module renders stays below 0.36. It exists to bound what a single source
    /// may weigh at worst; it never sizes an entry, which is always counted.
    /// </summary>
    internal const int MaxNodesPerSourceChar = 2;

    /// <summary>
    /// Longest source the memoization can promise to hold. A source that alone
    /// outweighs the budget is refused on arrival without a word and reparsed
    /// on every single call, which reads as a slow renderer and never as a
    /// misconfiguration, so the configured ceiling is bound to this one at
    /// startup. The budget no longer is that ceiling: it is denominated in
    /// bytes, and a character is worth two of them as text and up to a couple
    /// of hundred as tree, so what bounds the ceiling is the worst case.
    /// </summary>
    internal const int MaxMemoizableSourceChars = (int)((MaxResidentBytes - EntryOverheadBytes)
        / (BytesPerSourceChar + (BytesPerNode * MaxNodesPerSourceChar)));

    /// <summary>Passes one admission spends freeing room before it gives up.</summary>
    private const int CompactionPasses = 8;

    /// <summary>Factor by which each pass grows what the pass before it asked for.</summary>
    private const double CompactionGrowth = 4;

    private readonly MemoryCache _templates = new(Options.Create(new MemoryCacheOptions
    {
        SizeLimit = MaxResidentBytes,
        CompactionPercentage = CompactionShare,

        // The store's own accounting is what its admission gate reads. A count
        // kept beside it would be a second opinion, free to disagree with the
        // gate exactly when the budget is full, which is the only moment either
        // number decides anything.
        TrackStatistics = true,
    }));

    private long _hits;
    private long _parses;

    /// <summary>How many lookups were answered without parsing. Observability for tests.</summary>
    internal long Hits => Interlocked.Read(ref _hits);

    /// <summary>How many lookups had to parse. Observability for tests.</summary>
    internal long Parses => Interlocked.Read(ref _parses);

    /// <summary>Managed bytes resident right now, as the store's own gate counts them.</summary>
    internal long ResidentBytes => _templates.GetCurrentStatistics()?.CurrentEstimatedSize ?? 0;

    /// <summary>Sources resident right now.</summary>
    internal long ResidentEntries => _templates.GetCurrentStatistics()?.CurrentEntryCount ?? 0;

    /// <summary>
    /// What one entry weighs: the source the tree keeps alive by slices, the
    /// tree itself, and the store's bookkeeping around both.
    /// </summary>
    internal static long WeightOf(string source, Template template)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ((long)source.Length * BytesPerSourceChar)
            + ((long)CountNodes(template) * BytesPerNode)
            + EntryOverheadBytes;
    }

    /// <summary>
    /// Nodes in the parsed tree. The walk carries its own stack on the heap and
    /// never recurses: a few thousand nested member accesses parse clean, weigh
    /// nine kilobytes, sit far inside every size limit this module imposes, and
    /// overflow the stack of a recursive walk. A stack overflow cannot be
    /// caught; it ends the process.
    /// </summary>
    internal static int CountNodes(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Page is not { } page)
        {
            return 0;
        }

        var pending = new Stack<ScriptNode>();
        pending.Push(page);
        var nodes = 0;
        while (pending.TryPop(out ScriptNode? node))
        {
            nodes++;
            var children = node.ChildrenCount;
            for (var index = 0; index < children; index++)
            {
                if (node.GetChildren(index) is { } child)
                {
                    pending.Push(child);
                }
            }
        }

        return nodes;
    }

    /// <summary>
    /// The parsed template for the source, when the store already holds it.
    /// </summary>
    /// <remarks>
    /// It exists so the caller can tell a hit from a miss before it acts. What
    /// runs between the two is the admission check that bounds a parse, and
    /// charging it to a hit would put the whole cost of that guard on the path
    /// this memoization exists to keep cheap: a source resident here was
    /// measured on the call that parsed it.
    /// </remarks>
    internal bool TryGet(string source, [NotNullWhen(true)] out Template? template)
    {
        if (_templates.TryGetValue(source, out var stored) && stored is Template cached)
        {
            Interlocked.Increment(ref _hits);
            template = cached;
            return true;
        }

        template = null;
        return false;
    }

    /// <summary>
    /// The parsed template for the source, from the memoization or from a fresh
    /// parse. A fresh parse is not kept here: only a render that finished over
    /// it may ask for that, through <see cref="Keep"/>.
    /// </summary>
    internal Template GetOrParse(string source)
    {
        if (_templates.TryGetValue(source, out var stored) && stored is Template cached)
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        Interlocked.Increment(ref _parses);
        return Template.Parse(source);
    }

    /// <summary>
    /// Keeps the parse of a source a render has just completed over. A template
    /// that parsed with errors and a source already resident are both no-ops:
    /// the error path has to stay visible on every call, and replacing an entry
    /// with itself would pay a walk of the whole tree for nothing.
    /// </summary>
    /// <remarks>
    /// Two callers that missed the same source at once each keep the tree they
    /// parsed, and the first one here is the one that stays. The tree is
    /// read-only during a render, so which of the two it is changes nothing for
    /// either caller.
    /// </remarks>
    internal void Keep(string source, Template template)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(template);
        if (template.HasErrors || _templates.TryGetValue(source, out _))
        {
            return;
        }

        var weight = WeightOf(source, template);
        MakeRoomFor(weight);
        _templates.Set(source, template, new MemoryCacheEntryOptions { Size = weight });
    }

    public void Dispose() => _templates.Dispose();

    /// <summary>
    /// Frees room for an arriving entry before it is offered. Left alone, the
    /// store answers a refusal with a compaction sized as a share of the
    /// budget, so once the budget is full an entry heavier than that share is
    /// refused for good: the heaviest source, the one that costs the most to
    /// parse, is exactly the one that would never be memoized again. Freeing
    /// what the arrival weighs turns that standstill back into eviction by
    /// least recent use, which is what the policy is for.
    /// </summary>
    /// <remarks>
    /// The compaction runs inline, on this thread, and never as the pool pass
    /// the store schedules for itself: a pass that is merely queued lets every
    /// concurrent admission race the same pending work and decide on a store
    /// that is about to change underneath it. A concurrent admission can still
    /// take the room this one just freed; the store then refuses the loser,
    /// which parses again on its next call and makes room again. Nothing
    /// freezes and the budget is never exceeded, because the store's own gate
    /// remains the last word.
    /// </remarks>
    private void MakeRoomFor(long arriving)
    {
        var dropping = 1d;
        for (var pass = 0; pass < CompactionPasses; pass++)
        {
            var entries = ResidentEntries;
            if (entries == 0 || ResidentBytes + arriving <= MaxResidentBytes)
            {
                return;
            }

            // The store compacts by share of the entry count and never by
            // weight, so what it frees is decided by what the least recently
            // used entries happen to weigh, which is not what the average one
            // weighs. Asking for the share the missing weight suggests is what
            // a first version did, and a measured run freed fifty megabytes to
            // take in four: the tail it dropped was made of the heaviest
            // entries in the store. So each pass asks for a few entries, checks
            // what that bought, and only then asks for more.
            _templates.Compact(Math.Min(1d, (dropping + 0.5) / entries));
            dropping *= CompactionGrowth;
        }
    }
}
