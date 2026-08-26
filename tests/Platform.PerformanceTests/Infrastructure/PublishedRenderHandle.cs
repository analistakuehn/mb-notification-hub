using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.PerformanceTests.Infrastructure;

/// <summary>
/// Drives the sandboxed template engine of TemplateManagement, which is
/// internal to the API assembly and not visible to this project.
/// </summary>
/// <remarks>
/// Binding it by name is the price of measuring the deployed engine instead of
/// a copy of it. One member cannot be bound into a delegate: the scoped render
/// takes the engine's own scope type as its first parameter, and no delegate
/// this project can name accepts it. Every render therefore goes through
/// reflected invocation, in both arms alike, so the arms stay comparable and
/// the versioned reference carries the same constant.
/// </remarks>
internal sealed class PublishedRenderHandle
{
    private const string EngineTypeName =
        "NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating.ScribanTemplateEngine";

    private const string ParseCacheTypeName =
        "NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating.ScribanParseCache";

    private const string LayoutValidationTypeName =
        "NotificationHub.Api.Modules.TemplateManagement.Domain.LayoutValidation";

    private readonly object _engine;
    private readonly MethodInfo _beginForm;
    private readonly MethodInfo _renderOnScope;
    private readonly MethodInfo _renderContent;
    private readonly MethodInfo _renderAlone;

    private PublishedRenderHandle(
        object engine,
        MethodInfo beginForm,
        MethodInfo renderOnScope,
        MethodInfo renderContent,
        MethodInfo renderAlone,
        string contentVariable)
    {
        _engine = engine;
        _beginForm = beginForm;
        _renderOnScope = renderOnScope;
        _renderContent = renderContent;
        _renderAlone = renderAlone;
        ContentVariable = contentVariable;
    }

    /// <summary>The single variable a layout reads, taken from the module itself.</summary>
    internal string ContentVariable { get; }

    internal static PublishedRenderHandle Create(TemplatingOptions options)
    {
        Assembly assembly = typeof(TemplateManagementDbContext).Assembly;
        Type engineType = assembly.GetType(EngineTypeName, throwOnError: true)!;
        Type parseCacheType = assembly.GetType(ParseCacheTypeName, throwOnError: true)!;
        Type layoutValidationType = assembly.GetType(LayoutValidationTypeName, throwOnError: true)!;
        var engine = Activator.CreateInstance(
            engineType, Options.Create(options), Activator.CreateInstance(parseCacheType))!;

        return new PublishedRenderHandle(
            engine,
            Bound(engineType, "BeginForm", 0),
            Bound(engineType, "RenderAsync", 4),
            Bound(engineType, "RenderContentAsync", 5),
            Bound(engineType, "RenderAsync", 3),
            (string)layoutValidationType
                .GetField("ContentPlaceholderVariable", BindingFlags.Static | BindingFlags.Public)!
                .GetRawConstantValue()!);
    }

    /// <summary>Opens the context the renders of one form share.</summary>
    internal object BeginForm() => _beginForm.Invoke(_engine, [])!;

    /// <summary>One field of a form, on the form's own context.</summary>
    internal string RenderField(object scope, string source, JsonElement? variables)
        => Value(_renderOnScope.Invoke(_engine, [scope, source, variables, CancellationToken.None]));

    /// <summary>One layout frame around a finished text, on the form's own context.</summary>
    internal string Wrap(object scope, string layoutSource, string content)
        => Value(_renderContent.Invoke(
            _engine, [scope, layoutSource, ContentVariable, content, CancellationToken.None]));

    /// <summary>One render on a context built for it alone, as the preview path renders.</summary>
    internal string RenderAlone(string source, JsonElement? variables)
        => Value(_renderAlone.Invoke(_engine, [source, variables, CancellationToken.None]));

    private static string Value(object? task)
    {
        Result<string> result = ((Task<Result<string>>)task!).GetAwaiter().GetResult();
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException(
                $"A sonda mede renders que passam; este falhou com: {result.Error}");
    }

    private static MethodInfo Bound(Type type, string name, int parameters)
        => Array.Find(
                type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
                candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal)
                    && candidate.GetParameters().Length == parameters)
            ?? throw new InvalidOperationException(
                $"O motor de template não expõe '{name}' com {parameters} parâmetros; "
                + "a sonda está fora de sincronia com o tipo.");
}
