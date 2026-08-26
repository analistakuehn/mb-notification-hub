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
        template.Page?.Accept(collector);
        return new TemplateSourceAnalysis(true, null, collector.UsedVariables());
    }

    internal Task<Result<string>> RenderAsync(
        string source,
        JsonElement? variables,
        CancellationToken cancellationToken)
        => Task.FromResult(Render(source, variables, cancellationToken));

    private Result<string> Render(string source, JsonElement? variables, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Length > options.Value.MaxTemplateSizeChars)
        {
            return Result.ValidationError<string>(SizeLimitMessage(source.Length));
        }

        // Published sources are immutable, so the parse memoizes per source
        // text; each render still gets its own context over the shared AST.
        Template template = parseCache.GetOrParse(source);
        if (template.HasErrors)
        {
            return Result.ValidationError<string>(
                string.Join(" ", template.Messages.Select(message => message.ToString())));
        }

        // One linked source carries both the caller's cancellation and the
        // wall-clock deadline, and the engine observes it at its own
        // checkpoints. The render therefore stops, rather than being abandoned
        // to a pool thread that keeps burning CPU long after the caller already
        // has its answer.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Value.RenderTimeoutMilliseconds);

        var output = new BoundedScriptOutput(options.Value.MaxOutputChars);
        var context = new TemplateContext(SandboxBuiltin)
        {
            LoopLimit = options.Value.LoopLimit,
            RecursiveLimit = options.Value.RecursionLimit,
            StrictVariables = true,
            MemberFilter = static _ => false,
            CancellationToken = deadline.Token,

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
        context.PushGlobal(BuildGlobals(variables));
        context.PushOutput(output);

        try
        {
            return Result.Success(template.Render(context));
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
                : Result.ValidationError<string>(DescribeFailure(exception, variables));
        }
    }

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
    private string DescribeFailure(ScriptRuntimeException exception, JsonElement? variables)
        => exception.InnerException is RegexMatchTimeoutException
            ? TimeLimitMessage()
            : RedactVariableValues(exception.Message, variables);

    /// <summary>
    /// Removes caller-supplied values from an engine message. Some engine
    /// diagnostics interpolate the offending value itself (a non-numeric
    /// argument is reported with its text), and that message travels to the
    /// HTTP boundary as problem detail.
    /// </summary>
    private static string RedactVariableValues(string message, JsonElement? variables)
    {
        if (variables is not { ValueKind: JsonValueKind.Object } root)
        {
            return message;
        }

        var redacted = message;
        foreach (var value in ScalarTexts(root))
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

    /// <summary>Builds the sandbox globals from a JSON object; only data crosses the boundary.</summary>
    private static ScriptObject BuildGlobals(JsonElement? variables)
    {
        var globals = new ScriptObject();
        if (variables is not { ValueKind: JsonValueKind.Object } root)
        {
            return globals;
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            globals.SetValue(property.Name, ToScriptValue(property.Value), readOnly: false);
        }

        return globals;
    }

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
    private sealed class GlobalVariableCollector : ScriptVisitor
    {
        private readonly HashSet<string> _reads = new(StringComparer.Ordinal);
        private readonly HashSet<string> _writes = new(StringComparer.Ordinal);

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
            => node.Target?.Accept(this);
    }
}
