using System.Text.Json;
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
internal sealed class ScribanTemplateEngine(IOptions<TemplatingOptions> options)
{
    /// <summary>Globals every template can read without declaring them (engine builtins).</summary>
    private static readonly string[] BuiltinGlobals =
        ["array", "blank", "date", "empty", "html", "include", "math", "object", "regex", "string", "timespan"];

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
            string messages = string.Join(" ", template.Messages.Select(message => message.ToString()));
            return new TemplateSourceAnalysis(false, messages, []);
        }

        var collector = new GlobalVariableCollector();
        template.Page?.Accept(collector);
        return new TemplateSourceAnalysis(true, null, collector.UsedVariables());
    }

    internal async Task<Result<string>> RenderAsync(
        string source,
        JsonElement? variables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Length > options.Value.MaxTemplateSizeChars)
        {
            return Result.ValidationError<string>(SizeLimitMessage(source.Length));
        }

        var template = Template.Parse(source);
        if (template.HasErrors)
        {
            return Result.ValidationError<string>(
                string.Join(" ", template.Messages.Select(message => message.ToString())));
        }

        using var renderCancellation = new CancellationTokenSource();
        var context = new TemplateContext
        {
            LoopLimit = options.Value.LoopLimit,
            RecursiveLimit = options.Value.RecursionLimit,
            StrictVariables = true,
            MemberFilter = static _ => false,
            CancellationToken = renderCancellation.Token,
        };
        context.PushGlobal(BuildGlobals(variables));

        var renderTask = Task.Run(() => template.Render(context));
        Task first = await Task.WhenAny(
                renderTask,
                Task.Delay(TimeSpan.FromMilliseconds(options.Value.RenderTimeoutMilliseconds), cancellationToken))
            .ConfigureAwait(false);
        if (first != renderTask)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Discard the in-flight render: the cancellation asks the engine to
            // stop, and the continuation only observes the eventual exception.
            await renderCancellation.CancelAsync().ConfigureAwait(false);
            _ = renderTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return Result.ValidationError<string>(
                $"Rendering exceeded the {options.Value.RenderTimeoutMilliseconds}ms time limit and was discarded.");
        }

        try
        {
            return Result.Success(await renderTask.ConfigureAwait(false));
        }
        catch (ScriptRuntimeException exception)
        {
            return Result.ValidationError<string>(exception.Message);
        }
    }

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
                return element.TryGetInt64(out long integer) ? integer : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
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
