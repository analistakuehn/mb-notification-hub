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
/// Sandboxed Scriban execution. Templates only ever see plain data built from
/// JSON: no .NET type is exposed and reflected member access is filtered out
/// entirely. Loop and recursion limits are native to the engine; the wall-clock
/// timeout is imposed externally by running the render in a task and discarding
/// its result when the deadline passes.
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
    /// own.
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
            cancellationToken));

    /// <summary>
    /// Renders one field of a published form on the context that form's renders
    /// share.
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
            cancellationToken));

    /// <summary>
    /// Renders a source over one finished text, exposed under the single
    /// variable name the caller gives. The globals are synthetic and carry no
    /// payload, so a layout that reads a template variable is refused exactly
    /// as it is with a payload the caller never passed. Going through JSON to
    /// say the same thing would escape every angle bracket of an HTML body,
    /// only to unescape it on the way back in.
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
            cancellationToken));

    /// <summary>
    /// Opens the execution context the renders of one form share. The caller
    /// owns the scope and lets it go with the form: this engine is a singleton,
    /// so a scope reachable from a field of it would put two notifications in
    /// the same context.
    /// </summary>
    internal FormRenderScope BeginForm() => new(this);

    private Result<string> Render(
        FormRenderScope? scope,
        string source,
        RenderInput input,
        TemplateProvenance provenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Length > options.Value.MaxTemplateSizeChars)
        {
            return Result.ValidationError<string>(SizeLimitMessage(source.Length));
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
        Template template = memoizable ? parseCache.GetOrParse(source) : Template.Parse(source);
        if (template.HasErrors)
        {
            return Result.ValidationError<string>(
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

            return Result.Success(rendered);
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
                return Result.ValidationError<string>(OutputLimitMessage());
            }

            // The engine reports cancellation as its own runtime error rather
            // than propagating the token's exception, so the token state is what
            // identifies the deadline, not the exception shape.
            return deadline.IsCancellationRequested
                ? Cancelled(cancellationToken)
                : Result.ValidationError<string>(DescribeFailure(exception, input));
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
    private Result<string> Cancelled(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Result.ValidationError<string>(TimeLimitMessage());
    }

    /// <summary>
    /// Builds the caller-facing failure text. Engine vocabulary (variable name,
    /// type name, limit, position) is what an author needs to fix a template;
    /// the values passed in are not, and at dispatch time they are the
    /// recipient's own data.
    /// </summary>
    private string DescribeFailure(ScriptRuntimeException exception, RenderInput input)
        => exception.InnerException is RegexMatchTimeoutException
            ? TimeLimitMessage()
            : RedactCallerValues(exception.Message, input);

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

        return builtin;
    }

    private static void RemoveMember(ScriptObject builtin, string group, string member)
    {
        if (builtin[group] is ScriptObject target)
        {
            target.Remove(member);
        }
    }

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
