using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NotificationHub.SharedKernel;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

/// <summary>Result of parsing one template source inside the sandbox.</summary>
internal sealed record TemplateSourceAnalysis(
    bool ParseSucceeded,
    string? ParseError,
    IReadOnlyList<string> UsedVariables);

/// <summary>
/// Where the source of one render comes from, which is what decides whether its
/// parse is worth keeping.
/// </summary>
/// <remarks>
/// The caller states it, and it is deliberately not read off the presence of a
/// form scope: the scope exists so the fields of one form can share an
/// execution context, and reading provenance from it would tie two decisions
/// that have nothing to do with each other.
/// </remarks>
internal enum TemplateProvenance
{
    /// <summary>
    /// Content the author is still editing. The same text is a different
    /// template one keystroke later, so its parse is worth nothing to the call
    /// after it.
    /// </summary>
    Draft,

    /// <summary>
    /// Published content, immutable per version: the same text is always the
    /// same template, and its parse is worth keeping.
    /// </summary>
    Published,
}

/// <summary>
/// Which sandbox limit turned one render away, or <see cref="None"/> when
/// nothing did.
/// </summary>
/// <remarks>
/// The engine knows the mode and never the identity of what it rendered; the
/// caller knows the identity and, without this, never the mode. That is the
/// whole reason the value exists: a refusal that reaches an operator as a
/// caller-facing sentence and nothing else cannot be told apart from any other
/// refusal of the same shape.
/// <para>
/// Two outcomes are deliberately outside this catalogue. The caller giving up
/// is its own control flow and leaves as an exception rather than a result, so
/// no mode describes it. A system failure (an allocation that could not be
/// served) propagates on purpose, because answering it on the caller-facing
/// axis would turn a memory event into a request the author wrote wrong.
/// </para>
/// </remarks>
internal enum TemplateRefusal
{
    /// <summary>The render finished and produced its text.</summary>
    None,

    /// <summary>
    /// The source is longer than the configured character ceiling, measured
    /// before anything parses it.
    /// </summary>
    SourceSize,

    /// <summary>The whole source carries more tokens than the ceiling admits.</summary>
    SourceTokens,

    /// <summary>One expression in the source carries more tokens than the ceiling admits.</summary>
    SourceCodeBlockTokens,

    /// <summary>The source is not a template the engine can parse.</summary>
    ParseFailed,

    /// <summary>
    /// The render crossed the wall-clock deadline. It covers the two doors that
    /// deadline can arrive through, and it has to: a catastrophic regular
    /// expression is bounded by the engine's own regex timeout and by the
    /// render deadline at the same number, so which of them fires first is
    /// decided by timer resolution and not by the source. Naming only one of
    /// them would lose half of those refusals, non-deterministically.
    /// </summary>
    TimeLimit,

    /// <summary>The accumulated output crossed the configured ceiling.</summary>
    OutputLimit,

    /// <summary>
    /// The render failed inside the engine for a reason this module cannot
    /// separate: the loop limit, the recursion limit, and an authoring mistake
    /// caught at render time (an undeclared variable, a member on nothing, an
    /// argument of the wrong kind) all arrive here.
    /// <para>
    /// The name promises less than the value carries on purpose. Scriban 7.2.6
    /// reports all of them as one exception type with no subtype, offers no
    /// usable hook to observe the limit counters, and hands back a node type
    /// that collides across the cases, so telling them apart would mean
    /// matching the engine's own English message text, which is exactly the
    /// coupling this value exists to remove. Calling the value
    /// <c>Runtime</c> or <c>Other</c> would read as a residual somebody chose
    /// not to describe; this one states what it holds.
    /// </para>
    /// </summary>
    Unclassified,
}

/// <summary>
/// What one render answers: the caller-facing result, unchanged, and beside it
/// the mode of the refusal that produced it.
/// </summary>
/// <remarks>
/// A struct because a form on the dispatch path renders every field through
/// this type and succeeds on all of them: the channel travels the hot path, so
/// it may not cost an allocation per field to carry a value the successful
/// render does not even use.
/// </remarks>
internal readonly record struct TemplateRenderOutcome(Result<string> Result, TemplateRefusal Refusal);

/// <summary>
/// Sandboxed Scriban execution. Templates only ever see plain data built from
/// JSON: no .NET type is exposed and reflected member access is filtered out
/// entirely. Loop and recursion limits are native to the engine, and the
/// wall-clock deadline is a linked cancellation token the engine observes at
/// its own checkpoints.
/// <para>
/// That deadline covers the render and only the render. The parse runs before
/// it and takes no cancellation token, so it is bounded the only way an
/// uninterruptible phase can be: the source is measured first and refused
/// before the parser ever sees it, per <see cref="ScribanSourceComplexity"/>.
/// </para>
/// </summary>
internal sealed class ScribanTemplateEngine(IOptions<TemplatingOptions> options, ScribanParseCache parseCache)
{
    /// <summary>Replaces a caller-supplied value that surfaced in an engine message.</summary>
    private const string RedactedValue = "***";

    /// <summary>
    /// Shortest value worth redacting from an engine message. One and two
    /// character values are substrings of ordinary engine vocabulary, so
    /// redacting them would destroy the message without protecting anything a
    /// four digit code is the shortest secret this module actually carries.
    /// </summary>
    private const int MinRedactableLength = 3;

    /// <summary>
    /// Builtin surface the sandbox exposes, derived once from the engine
    /// default and stripped of every member that turns data into code, loads an
    /// external template, or allocates by width outside the output ceiling.
    /// </summary>
    private static readonly ScriptObject SandboxBuiltin = BuildSandboxBuiltin();

    /// <summary>
    /// Globals every template can read without declaring them. Derived from the
    /// sandbox surface itself, so a member removed above stops being an
    /// undeclared-variable exemption in the same edit, and an engine upgrade
    /// that adds a builtin cannot drift from this list.
    /// </summary>
    private static readonly string[] BuiltinGlobals =
        [.. SandboxBuiltin.GetMembers().Order(StringComparer.Ordinal)];

    internal TemplateSourceAnalysis Analyze(string source, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);

        if (source.Length > options.Value.MaxTemplateSizeChars)
        {
            return new TemplateSourceAnalysis(false, SizeLimitMessage(source.Length), []);
        }

        SourceComplexityLimit exceeded = Exceeded(source);
        if (exceeded is not SourceComplexityLimit.None)
        {
            return new TemplateSourceAnalysis(false, ComplexityLimitMessage(exceeded), []);
        }

        var template = Template.Parse(source, sourcePath);
        if (template.HasErrors)
        {
            var messages = string.Join(" ", template.Messages.Select(message => message.ToString()));
            return new TemplateSourceAnalysis(false, messages, []);
        }

        var collector = new GlobalVariableCollector();
        collector.Collect(template.Page);
        return new TemplateSourceAnalysis(true, null, collector.UsedVariables());
    }

    /// <summary>
    /// Renders one source the author may still be editing, on a context of its
    /// own, and answers the mode of the refusal alongside the result.
    /// </summary>
    internal Task<TemplateRenderOutcome> RenderOutcomeAsync(
        string source,
        JsonElement? variables,
        CancellationToken cancellationToken)
        => Task.FromResult(Render(
            scope: null,
            source,
            RenderInput.OfPayload(variables),
            provenance: TemplateProvenance.Draft,
            cancellationToken));

    /// <summary>
    /// Renders one field of a published form on the context that form's renders
    /// share, and answers the mode of the refusal alongside the result.
    /// </summary>
    internal Task<TemplateRenderOutcome> RenderOutcomeAsync(
        FormRenderScope scope,
        string source,
        JsonElement? variables,
        CancellationToken cancellationToken)
        => Task.FromResult(Render(
            scope,
            source,
            RenderInput.OfPayload(variables),
            provenance: TemplateProvenance.Published,
            cancellationToken));

    /// <summary>
    /// Renders a source over one finished text, exposed under the single
    /// variable name the caller gives, and answers the mode of the refusal
    /// alongside the result. The globals are synthetic and carry no payload, so
    /// a layout that reads a template variable is refused exactly as it is with
    /// a payload the caller never passed. Going through JSON to say the same
    /// thing would escape every angle bracket of an HTML body, only to unescape
    /// it on the way back in.
    /// </summary>
    internal Task<TemplateRenderOutcome> RenderContentOutcomeAsync(
        FormRenderScope scope,
        string source,
        string variableName,
        string content,
        CancellationToken cancellationToken)
        => Task.FromResult(Render(
            scope,
            source,
            RenderInput.OfContent(variableName, content),
            provenance: TemplateProvenance.Published,
            cancellationToken));

    /// <summary>
    /// The text axis of a draft render, for a caller that reports no refusal of
    /// its own and only needs what the render produced.
    /// </summary>
    internal Task<Result<string>> RenderAsync(
        string source,
        JsonElement? variables,
        CancellationToken cancellationToken)
        => Task.FromResult(Render(
            scope: null,
            source,
            RenderInput.OfPayload(variables),
            provenance: TemplateProvenance.Draft,
            cancellationToken).Result);

    /// <summary>
    /// The text axis of one field of a published form, for a caller that
    /// reports no refusal of its own.
    /// </summary>
    internal Task<Result<string>> RenderAsync(
        FormRenderScope scope,
        string source,
        JsonElement? variables,
        CancellationToken cancellationToken)
        => Task.FromResult(Render(
            scope,
            source,
            RenderInput.OfPayload(variables),
            provenance: TemplateProvenance.Published,
            cancellationToken).Result);

    /// <summary>
    /// The text axis of a render over one finished text, for a caller that
    /// reports no refusal of its own.
    /// </summary>
    internal Task<Result<string>> RenderContentAsync(
        FormRenderScope scope,
        string source,
        string variableName,
        string content,
        CancellationToken cancellationToken)
        => Task.FromResult(Render(
            scope,
            source,
            RenderInput.OfContent(variableName, content),
            provenance: TemplateProvenance.Published,
            cancellationToken).Result);

    /// <summary>
    /// Opens the execution context the renders of one form share. The caller
    /// owns the scope and lets it go with the form: this engine is a singleton,
    /// so a scope reachable from a field of it would put two notifications in
    /// the same context.
    /// </summary>
    internal FormRenderScope BeginForm() => new(this);

    private TemplateRenderOutcome Render(
        FormRenderScope? scope,
        string source,
        RenderInput input,
        TemplateProvenance provenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Length > options.Value.MaxTemplateSizeChars)
        {
            return Refused(TemplateRefusal.SourceSize, SizeLimitMessage(source.Length));
        }

        // Published sources are immutable, so their parse memoizes per source
        // text; the shared AST is read-only during a render, and every render
        // still pushes its own data and its own buffer over it. A draft is
        // parsed and dropped: the same text is a different template one
        // keystroke later, and memoizing it would spend the budget on content
        // nobody will ask for twice.
        //
        // One consequence is a simplification and not a side effect: the
        // memoization of the API host stays empty for good, because preview is
        // the only writer there, and memoizing parses becomes an artifact of
        // the workers alone.
        var memoizable = provenance == TemplateProvenance.Published;
        Template? template = null;
        if (memoizable)
        {
            parseCache.TryGet(source, out template);
        }

        if (template is null)
        {
            // Everything below this point is covered by the deadline. The parse
            // is not: it takes no cancellation token, and the deadline starts
            // only once it has returned. So a source that would be expensive to
            // parse is refused before the parser is called, which is the one
            // moment that cost can still be declined.
            //
            // The measurement is charged to the call that parses and never to
            // the one that looks up: a resident source was measured when it was
            // parsed. A draft therefore pays it on every call, which is exactly
            // where it has to be paid, because a draft is never memoized and
            // preview is the surface an author drives at will.
            SourceComplexityLimit exceeded = Exceeded(source);
            if (exceeded is not SourceComplexityLimit.None)
            {
                return Refused(RefusalFor(exceeded), ComplexityLimitMessage(exceeded));
            }

            template = memoizable ? parseCache.GetOrParse(source) : Template.Parse(source);
        }

        if (template.HasErrors)
        {
            return Refused(
                TemplateRefusal.ParseFailed,
                string.Join(" ", template.Messages.Select(message => message.ToString())));
        }

        // One linked source carries both the caller's cancellation and the
        // wall-clock deadline, and the engine observes it at its own
        // checkpoints. The render therefore stops, rather than being abandoned
        // to a pool thread that keeps burning CPU long after the caller already
        // has its answer. The deadline belongs to the render and never to the
        // form: one source shared by every field would charge each field with
        // the time its predecessors spent.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Value.RenderTimeoutMilliseconds);

        // A single source renders on a scope of its own. The context is built
        // the same way either way; what a form buys by passing one in is
        // building it once instead of once per field.
        FormRenderScope active = scope ?? BeginForm();
        var output = new BoundedScriptOutput(options.Value.MaxOutputChars);
        active.BeginRender(input.Globals(), output, deadline.Token);

        try
        {
            var rendered = template.Render(active.Context);

            // Only a render that finished puts its source in the memoization. A
            // source the engine refused would otherwise be answered from memory
            // on every later call and charged to the budget, while nobody can
            // render it.
            if (memoizable)
            {
                parseCache.Keep(source, template);
            }

            return new TemplateRenderOutcome(Result.Success(rendered), TemplateRefusal.None);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(cancellationToken);
        }
        // The engine wraps every exception it meets, including a system one. An
        // allocation failure is not something the author wrote wrong, so it
        // must not come back as a validation error: that would answer a memory
        // event with a 400 and hide it from every operational signal.
        catch (ScriptRuntimeException exception) when (exception.InnerException is not OutOfMemoryException)
        {
            // The ceiling is a definite cause and outranks the deadline, which
            // an oversized render tends to cross on its way out anyway.
            if (output.LimitExceeded)
            {
                return Refused(TemplateRefusal.OutputLimit, OutputLimitMessage());
            }

            // The engine reports cancellation as its own runtime error rather
            // than propagating the token's exception, so the token state is what
            // identifies the deadline, not the exception shape.
            return deadline.IsCancellationRequested
                ? Cancelled(cancellationToken)
                : DescribeFailure(exception, input);
        }
        finally
        {
            active.EndRender();
        }
    }

    /// <summary>
    /// Builds one sandboxed execution context. Its constructor eagerly fills a
    /// pool of reflection argument arrays sized by the engine's own parameter
    /// ceiling, so the cost is the same for the shortest subject and for the
    /// richest body, and it dominates what a small render costs.
    /// </summary>
    private TemplateContext NewContext() => new(SandboxBuiltin)
    {
        LoopLimit = options.Value.LoopLimit,
        RecursiveLimit = options.Value.RecursionLimit,
        StrictVariables = true,
        MemberFilter = static _ => false,

        // Deliberately one character above the sink's ceiling. The sink is
        // what must stop an oversized render, because it fails; the engine's
        // own limit truncates and appends an ellipsis, which would ship a
        // silently corrupted message that still passes normalization,
        // hashing and audit as if it were complete.
        LimitToString = options.Value.MaxOutputChars + 1,

        // Aligned with the wall-clock deadline: a catastrophic regex stops
        // burning the thread at the same moment the caller gives up.
        RegexTimeOut = TimeSpan.FromMilliseconds(options.Value.RenderTimeoutMilliseconds),
    };

    /// <summary>
    /// Separates the two reasons a render stops early. The caller giving up is
    /// its own control flow and propagates; the deadline is an expected outcome
    /// of this module and stays on the <c>Result</c> axis.
    /// </summary>
    private TemplateRenderOutcome Cancelled(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Refused(TemplateRefusal.TimeLimit, TimeLimitMessage());
    }

    /// <summary>
    /// Builds the caller-facing failure text. Engine vocabulary (variable name,
    /// type name, limit, position) is what an author needs to fix a template;
    /// the values passed in are not, and at dispatch time they are the
    /// recipient's own data.
    /// <para>
    /// This is the second door the wall-clock deadline arrives through: a
    /// catastrophic regular expression is stopped by the engine's own regex
    /// timeout, set to the same number as the render deadline, so which of the
    /// two fires is decided by timer resolution. Everything else the engine
    /// raises here is one exception type with no subtype, which is why it
    /// leaves under a single mode.
    /// </para>
    /// </summary>
    private TemplateRenderOutcome DescribeFailure(ScriptRuntimeException exception, RenderInput input)
        => exception.InnerException is RegexMatchTimeoutException
            ? Refused(TemplateRefusal.TimeLimit, TimeLimitMessage())
            : Refused(TemplateRefusal.Unclassified, RedactCallerValues(exception.Message, input));

    /// <summary>
    /// One refusal, on the caller-facing axis it has always travelled and with
    /// the mode beside it. The text is built exactly as before: a sibling module
    /// compares the whole error string for equality, so nothing here may reword
    /// it.
    /// </summary>
    private static TemplateRenderOutcome Refused(TemplateRefusal refusal, string message)
        => new(Result.ValidationError<string>(message), refusal);

    /// <summary>The mode that names an admission ceiling the source crossed.</summary>
    private static TemplateRefusal RefusalFor(SourceComplexityLimit exceeded)
        => exceeded is SourceComplexityLimit.CodeBlockTokens
            ? TemplateRefusal.SourceCodeBlockTokens
            : TemplateRefusal.SourceTokens;

    /// <summary>
    /// Removes caller-supplied values from an engine message. Some engine
    /// diagnostics interpolate the offending value itself (a non-numeric
    /// argument is reported with its text), and that message travels to the
    /// HTTP boundary as problem detail.
    /// </summary>
    private static string RedactCallerValues(string message, RenderInput input)
    {
        var redacted = message;
        foreach (var value in input.CallerTexts())
        {
            if (value.Length >= MinRedactableLength && redacted.Contains(value, StringComparison.Ordinal))
            {
                redacted = redacted.Replace(value, RedactedValue, StringComparison.Ordinal);
            }
        }

        return redacted;
    }

    /// <summary>Every scalar the payload carries, at any depth, as written.</summary>
    private static IEnumerable<string> ScalarTexts(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    foreach (var text in ScalarTexts(property.Value))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    foreach (var text in ScalarTexts(item))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonValueKind.String:
                if (element.GetString() is { Length: > 0 } value)
                {
                    yield return value;
                }

                break;
            case JsonValueKind.Number:
                yield return element.GetRawText();
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Derives the sandbox builtin surface from the engine default, then removes
    /// what the module's static checks cannot see through. The deep clone keeps
    /// the edit local to this sandbox instead of mutating the engine default.
    /// </summary>
    private static ScriptObject BuildSandboxBuiltin()
    {
        var builtin = (ScriptObject)new TemplateContext().BuiltinObject.Clone(deep: true);

        // Data must never become code. Both members evaluate a string as a
        // template or an expression at render time, so a published template can
        // carry an effective body that no validation check ever saw.
        RemoveMember(builtin, "object", "eval");
        RemoveMember(builtin, "object", "eval_template");

        // Width-based allocation escapes the output ceiling: the string is
        // built in full before a single character reaches the bounded sink.
        RemoveMember(builtin, "string", "pad_left");
        RemoveMember(builtin, "string", "pad_right");

        // No template loads another template inside this sandbox.
        builtin.Remove("include");
        builtin.Remove("include_join");

        // Last, because a sealed object refuses the removals above.
        Seal(builtin);

        return builtin;
    }

    /// <summary>
    /// Closes the shared surface to writes, at the root and at every group
    /// under it.
    /// </summary>
    /// <remarks>
    /// There is one of these objects per process, and the engine pushes it to
    /// the bottom of the global stack of every context and preserves it across
    /// the reset that runs between two renders. Anything written into it
    /// therefore outlives the render, the caller and the engine instance: a
    /// published template of one application would store a recipient's value
    /// there and a template of another application would read it, and
    /// overwriting the default date pattern would move every implicit date of
    /// every later render in the process.
    /// <para>
    /// The engine already refuses to replace a builtin function, and it
    /// already refuses a write that resolves to the root, because every render
    /// pushes globals of its own above it. What was left open, and what this
    /// closes, is member assignment inside a group: the target of that
    /// assignment is a member expression, which the module's own static check
    /// does not report, so the publication report of such a template comes back
    /// clean.
    /// </para>
    /// <para>
    /// Sealing the root is redundant against the pinned engine and is kept so
    /// that the surface carries one rule rather than two, and so that a release
    /// which stops shadowing the root does not reopen the hole silently.
    /// </para>
    /// </remarks>
    private static void Seal(ScriptObject builtin)
    {
        foreach (var member in builtin.GetMembers())
        {
            if (builtin[member] is ScriptObject group)
            {
                group.IsReadOnly = true;
            }
        }

        builtin.IsReadOnly = true;
    }

    private static void RemoveMember(ScriptObject builtin, string group, string member)
    {
        if (builtin[group] is ScriptObject target)
        {
            target.Remove(member);
        }
    }

    /// <summary>The first admission ceiling this source crosses, if any.</summary>
    private SourceComplexityLimit Exceeded(string source)
        => ScribanSourceComplexity.Exceeded(
            source,
            options.Value.MaxTemplateTokens,
            options.Value.MaxCodeBlockTokens);

    private string ComplexityLimitMessage(SourceComplexityLimit exceeded)
        => exceeded is SourceComplexityLimit.CodeBlockTokens
            ? $"A single template expression exceeds the {options.Value.MaxCodeBlockTokens} token limit."
            : $"The template exceeds the {options.Value.MaxTemplateTokens} token limit.";

    private string TimeLimitMessage()
        => $"Rendering exceeded the {options.Value.RenderTimeoutMilliseconds}ms time limit and was discarded.";

    private string OutputLimitMessage()
        => $"The rendered output exceeded the {options.Value.MaxOutputChars} character limit and was discarded.";

    private string SizeLimitMessage(int length)
        => $"The template has {length} characters and exceeds the {options.Value.MaxTemplateSizeChars} character limit.";

    private static object? ToScriptValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var nested = new ScriptObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    nested.SetValue(property.Name, ToScriptValue(property.Value), readOnly: false);
                }

                return nested;
            case JsonValueKind.Array:
                var items = new ScriptArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    items.Add(ToScriptValue(item));
                }

                return items;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var integer) ? integer : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary>
    /// What one render exposes to the template, and, from the very same source,
    /// which caller-supplied texts a failure message must not echo back. The
    /// two answers come from one place on purpose: globals built from one input
    /// while redaction reads another is how a recipient's own data reaches an
    /// error message.
    /// </summary>
    private readonly struct RenderInput
    {
        private readonly JsonElement? _payload;
        private readonly string? _variableName;
        private readonly string? _content;

        private RenderInput(JsonElement? payload, string? variableName, string? content)
        {
            _payload = payload;
            _variableName = variableName;
            _content = content;
        }

        /// <summary>The caller's payload, as a JSON object or as nothing at all.</summary>
        internal static RenderInput OfPayload(JsonElement? variables) => new(variables, null, null);

        /// <summary>One finished text under one name, and nothing else in scope.</summary>
        internal static RenderInput OfContent(string variableName, string content)
            => new(null, variableName, content);

        /// <summary>Builds the sandbox globals; only data crosses the boundary.</summary>
        internal ScriptObject Globals()
        {
            var globals = new ScriptObject();
            if (_variableName is not null)
            {
                globals.SetValue(_variableName, _content, readOnly: false);
                return globals;
            }

            if (_payload is not { ValueKind: JsonValueKind.Object } root)
            {
                return globals;
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                globals.SetValue(property.Name, ToScriptValue(property.Value), readOnly: false);
            }

            return globals;
        }

        /// <summary>Every text the caller supplied to this render, as written.</summary>
        internal IEnumerable<string> CallerTexts()
        {
            if (_content is not null)
            {
                return [_content];
            }

            return _payload is { ValueKind: JsonValueKind.Object } root ? ScalarTexts(root) : [];
        }
    }

    /// <summary>
    /// The sandboxed context that the renders of one form share, and the only
    /// thing they share.
    /// </summary>
    /// <remarks>
    /// Each render pushes its own globals, its own output sink and its own
    /// deadline, and pops all three before it returns, so no field reads the
    /// data of another one, writes into its buffer or spends its time budget.
    /// Sharing the globals instead would hand the layout render the payload the
    /// body was rendered with, and a layout that reads a template variable
    /// would resolve it silently instead of being refused; sharing the sink
    /// would append each field to the previous one, because the engine only
    /// clears a buffer it owns.
    /// <para>
    /// The type is not thread-safe and is not shareable: one scope belongs to
    /// the call frame that renders one form, and two forms never meet in one.
    /// </para>
    /// </remarks>
    internal sealed class FormRenderScope
    {
        private readonly int _globalDepth;
        private readonly int _outputDepth;

        internal FormRenderScope(ScribanTemplateEngine engine)
        {
            ArgumentNullException.ThrowIfNull(engine);
            Context = engine.NewContext();
            _globalDepth = Context.GlobalCount;
            _outputDepth = Context.OutputCount;
        }

        internal TemplateContext Context { get; }

        /// <summary>Puts everything one render owns on the context.</summary>
        internal void BeginRender(ScriptObject globals, IScriptOutput output, CancellationToken deadline)
        {
            Context.CancellationToken = deadline;
            Context.PushGlobal(globals);
            Context.PushOutput(output);
        }

        /// <summary>
        /// Takes it all back off, including the deadline, which is expired by
        /// the time the next render starts. The depth is verified rather than
        /// assumed: an engine that returned without balancing its own frames
        /// would leave this render's data resident and the next one reading it.
        /// </summary>
        internal void EndRender()
        {
            Context.PopOutput();
            Context.PopGlobal();
            Context.CancellationToken = CancellationToken.None;
            if (Context.OutputCount != _outputDepth || Context.GlobalCount != _globalDepth)
            {
                throw new InvalidOperationException(
                    "The render left the sandbox context unbalanced, which would expose the data "
                    + "of one field to the next one.");
            }

            // Popping is not enough. The engine counts the characters it has
            // written against its own output limit on the context, not on the
            // sink, and that count only clears here: without this, the fields
            // of one form would share a single budget and the engine would
            // start truncating a field and marking it with an ellipsis, which
            // is exactly the silently incomplete message the limits above are
            // set to prevent. The call is safe because the frames are balanced
            // and the context's bottom output is the one the engine created.
            Context.Reset();
        }
    }

    /// <summary>
    /// Output sink with a hard ceiling: the render aborts the moment the
    /// accumulated output crosses the configured limit, instead of letting a
    /// loop multiply fragments into an unbounded buffer.
    /// </summary>
    private sealed class BoundedScriptOutput(int maxChars) : IScriptOutput
    {
        private readonly StringBuilder _builder = new();

        internal bool LimitExceeded { get; private set; }

        public void Write(string text, int offset, int count)
        {
            if (_builder.Length + count > maxChars)
            {
                LimitExceeded = true;
                throw new InvalidOperationException(
                    $"The rendered output exceeded the {maxChars} character limit.");
            }

            _builder.Append(text, offset, count);
        }

        public ValueTask WriteAsync(string text, int offset, int count, CancellationToken cancellationToken)
        {
            Write(text, offset, count);
            return ValueTask.CompletedTask;
        }

        public override string ToString() => _builder.ToString();
    }

    /// <summary>
    /// Collects the global variables a template reads. Loop variables, local
    /// assignments, function names and member names do not count as usage;
    /// engine builtins are excluded because no schema declares them.
    /// </summary>
    /// <remarks>
    /// The walk keeps the nodes it still owes on a stack in the heap and never
    /// descends into itself. The engine parses a postfix chain such as
    /// <c>a.b.b.b</c> in a loop instead of a recursion, so it accepts one as
    /// deep as the source ceiling allows and hands back a tree just as deep; a
    /// walk that recursed over that tree ran out of call stack far inside the
    /// ceiling. A stack overflow is not catchable in .NET, and this walk reads
    /// source an author submits for validation, so the whole process would go
    /// down on a source the module accepts.
    /// <para>
    /// No source reaches that depth today. Every source is measured before it
    /// is parsed, and <see cref="TemplatingOptions.MaxCodeBlockTokens"/> caps
    /// one expression at 512 tokens, which is 255 links of <c>a.b.b</c>. What
    /// the parser does recurse over is capped by its own statement depth limit
    /// of 250 levels, and it refuses a source outright once the stack left runs
    /// low. Measured together, those two gates admit a tree about a thousand
    /// nodes deep, against the nine thousand links that exhaust a one megabyte
    /// stack here, so the walk below has no reachable trigger and is defense in
    /// depth. It stays iterative all the same: the first of those gates is a
    /// configuration value, and a walk written to assume it would fail the
    /// moment it moved.
    /// </para>
    /// <para>
    /// Dispatch stays the engine's own: every node type reaches the handler it
    /// reached before, including the ones that only look like the types handled
    /// below. What changed is where the pending nodes wait.
    /// </para>
    /// </remarks>
    private sealed class GlobalVariableCollector : ScriptVisitor
    {
        private readonly HashSet<string> _reads = new(StringComparer.Ordinal);
        private readonly HashSet<string> _writes = new(StringComparer.Ordinal);

        /// <summary>Nodes reached and not yet handled, in the heap.</summary>
        private readonly Stack<ScriptNode> _pending = new();

        /// <summary>
        /// Walks the tree rooted at the node given and records what it names.
        /// </summary>
        public void Collect(ScriptNode? root)
        {
            Enqueue(root);
            while (_pending.TryPop(out ScriptNode? node))
            {
                node.Accept(this);
            }
        }

        public List<string> UsedVariables()
            => _reads.Except(_writes, StringComparer.Ordinal)
                .Except(BuiltinGlobals, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

        public override void Visit(ScriptVariableGlobal node)
        {
            _reads.Add(node.Name);
            base.Visit(node);
        }

        public override void Visit(ScriptForStatement node)
        {
            if (node.Variable is ScriptVariable variable)
            {
                _writes.Add(variable.Name);
            }

            base.Visit(node);
        }

        public override void Visit(ScriptAssignExpression node)
        {
            if (node.Target is ScriptVariable variable)
            {
                _writes.Add(variable.Name);
            }

            base.Visit(node);
        }

        public override void Visit(ScriptFunction node)
        {
            if (node.NameOrDoToken is ScriptVariable variable)
            {
                _writes.Add(variable.Name);
            }

            if (node.Parameters is not null)
            {
                foreach (ScriptParameter parameter in node.Parameters)
                {
                    if (parameter.Name is not null)
                    {
                        _writes.Add(parameter.Name.Name);
                    }
                }
            }

            base.Visit(node);
        }

        public override void Visit(ScriptMemberExpression node)
            // Only the target counts: 'user.name' reads the variable 'user',
            // and 'name' is a member of it, not a template variable.
            => Enqueue(node.Target);

        /// <summary>
        /// Reaches a node without descending into it. Every generic path the
        /// base class offers a caller lands here, so none of them can turn the
        /// walk back into a recursion.
        /// </summary>
        public override void Visit(ScriptNode? node) => Enqueue(node);

        public override void Visit(ScriptList? list) => DefaultVisit(list);

        /// <summary>
        /// The point every node type funnels into once its own handler is done.
        /// Children are stacked back to front so they come out in source order,
        /// which keeps the visit order the recursive walk had.
        /// </summary>
        protected override void DefaultVisit(ScriptNode? node)
        {
            if (node is null)
            {
                return;
            }

            for (var index = node.ChildrenCount - 1; index >= 0; index--)
            {
                Enqueue(node.GetChildren(index));
            }
        }

        private void Enqueue(ScriptNode? node)
        {
            if (node is not null)
            {
                _pending.Push(node);
            }
        }
    }
}
