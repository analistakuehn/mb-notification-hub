using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.PerformanceTests.Infrastructure;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>What one arm of the render probe measured.</summary>
internal sealed record RenderCostArm(
    string ArmId,
    int Forms,
    long BytesPerForm,
    double MicrosecondsPerForm);

/// <summary>Everything one render run produced.</summary>
internal sealed record RenderCostOutcome(
    string RecordedAtUtc,
    string Host,
    int Processors,
    string Runtime,
    IReadOnlyList<RenderCostArm> Arms);

/// <summary>
/// What one notification form costs to render, in bytes allocated and in time.
/// <para>
/// A form is the unit the deployed renderer works in: subject, body and text
/// variant, each with the caller's payload, then the pinned layout wrapped
/// around the two bodies. Five renders, one notification. The probe reports the
/// same form twice, once on the context the fields share and once on a context
/// per render, because the second arm is the reference that makes the first one
/// mean something on any host.
/// </para>
/// <para>
/// Allocated bytes are what the gate reads. They do not depend on the machine,
/// on how loaded it is or on how many cores it has, which is what makes a
/// versioned reference honest here; the elapsed time is reported for the record
/// and gated by nothing.
/// </para>
/// </summary>
internal static class PublishedRenderCostScenario
{
    /// <summary>The arm the gate reads: one context for the whole form.</summary>
    internal const string SharedArm = "F1";

    /// <summary>The reference arm: one context per render, as a single preview renders.</summary>
    internal const string SeparateArm = "F0";

    private const string SubjectSource = "Pedido {{ order.id }} atualizado";

    private const string BodySource =
        "<p>Olá {{ user.name }}, o pedido {{ order.id }} tem {{ order.items.size }} itens.</p>"
        + "<ul>{{ for item in order.items }}<li>{{ item.label }}: {{ item.qty }}</li>{{ end }}</ul>";

    private const string BodyTextSource = "Olá {{ user.name }}, o pedido {{ order.id }} foi atualizado.";

    private const string LayoutBodySource =
        "<html><header>MB</header>{{ content }}<footer>rodapé</footer></html>";

    private const string LayoutBodyTextSource = "MB\n{{ content }}\nrodapé";

    private const string PayloadJson =
        """
        {
          "user": { "name": "Ana" },
          "order": { "id": "42", "items": [ { "label": "Café", "qty": 2 }, { "label": "Chá", "qty": 1 } ] }
        }
        """;

    internal static RenderCostOutcome Run(int forms, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfLessThan(forms, 1);

        var handle = PublishedRenderHandle.Create(new TemplatingOptions());
        using var document = JsonDocument.Parse(PayloadJson);
        JsonElement payload = document.RootElement.Clone();

        // A discarded pass first, one per arm below as well: it pays for the
        // parse cache, the cold plan of the delegates and the tiered
        // recompilation, none of which belong to the cost of a form.
        Drive(handle, payload, shared: true, forms: 32);
        Drive(handle, payload, shared: false, forms: 32);

        report($"Braço {SharedArm}: {forms:N0} formas, um contexto por forma.");
        RenderCostArm sharedArm = Measure(SharedArm, handle, payload, shared: true, forms: forms);
        Describe(sharedArm, report);

        report($"Braço {SeparateArm}: {forms:N0} formas, um contexto por render.");
        RenderCostArm separateArm = Measure(SeparateArm, handle, payload, shared: false, forms: forms);
        Describe(separateArm, report);

        return new RenderCostOutcome(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Environment.MachineName,
            Environment.ProcessorCount,
            Environment.Version.ToString(),
            [sharedArm, separateArm]);
    }

    private static void Describe(RenderCostArm arm, Action<string> report)
        => report(string.Create(
            CultureInfo.InvariantCulture,
            $"  {arm.BytesPerForm:N0} bytes por forma, {arm.MicrosecondsPerForm:N2} us por forma."));

    private static RenderCostArm Measure(
        string armId,
        PublishedRenderHandle handle,
        JsonElement payload,
        bool shared,
        int forms)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        Drive(handle, payload, shared, forms);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;

        return new RenderCostArm(
            armId,
            forms,
            bytes / forms,
            elapsed.TotalMicroseconds / forms);
    }

    /// <summary>
    /// Renders the form as the deployed renderer does: three fields over the
    /// payload, then the layout around the two bodies, with the subject left
    /// unframed.
    /// </summary>
    private static void Drive(PublishedRenderHandle handle, JsonElement payload, bool shared, int forms)
    {
        for (var form = 0; form < forms; form++)
        {
            if (shared)
            {
                var scope = handle.BeginForm();
                handle.RenderField(scope, SubjectSource, payload);
                var body = handle.RenderField(scope, BodySource, payload);
                var bodyText = handle.RenderField(scope, BodyTextSource, payload);
                handle.Wrap(scope, LayoutBodySource, body);
                handle.Wrap(scope, LayoutBodyTextSource, bodyText);
            }
            else
            {
                handle.RenderAlone(SubjectSource, payload);
                var body = handle.RenderAlone(BodySource, payload);
                var bodyText = handle.RenderAlone(BodyTextSource, payload);
                handle.RenderAlone(LayoutBodySource, SerializedContent(handle, body));
                handle.RenderAlone(LayoutBodyTextSource, SerializedContent(handle, bodyText));
            }
        }
    }

    /// <summary>The globals the layout frame used to be handed, built through JSON.</summary>
    private static JsonElement SerializedContent(PublishedRenderHandle handle, string rendered)
        => JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            [handle.ContentVariable] = rendered,
        });
}
