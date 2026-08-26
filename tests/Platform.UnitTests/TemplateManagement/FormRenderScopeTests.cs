using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// What the fields of one form share when they render on one context, and,
/// far more important, what they must never share. The context is a cost, not
/// a channel: the moment data, a buffer or a deadline crosses from one field
/// to the next, a template transformation can carry a value the caller never
/// exposed to that field.
/// </summary>
public sealed class FormRenderScopeTests
{
    private const string SensitiveValue = "123.456.789-09";

    private const string SubjectSource = "Pedido {{ order.id }} atualizado";

    private const string BodySource =
        "<p>Olá {{ user.name }}, o pedido {{ order.id }} tem {{ order.items.size }} itens.</p>"
        + "<ul>{{ for item in order.items }}<li>{{ item.label }}: {{ item.qty }}</li>{{ end }}</ul>";

    private const string BodyTextSource = "Olá {{ user.name }}, o pedido {{ order.id }} foi atualizado.";

    private const string LayoutBodySource =
        "<html><header>MB</header>{{ content }}<footer>rodapé</footer></html>";

    private const string LayoutBodyTextSource = "MB\n{{ content }}\nrodapé";

    private const string Payload =
        """
        {
          "user": { "name": "Ana" },
          "order": { "id": "42", "items": [ { "label": "Café", "qty": 2 }, { "label": "Chá", "qty": 1 } ] }
        }
        """;

    private static ScribanTemplateEngine Engine(
        int loopLimit = 1000,
        int recursionLimit = 16,
        int maxSizeChars = 10_000,
        int timeoutMs = 2000,
        int maxOutputChars = 1_000_000)
        => new(Options.Create(new TemplatingOptions
        {
            LoopLimit = loopLimit,
            RecursionLimit = recursionLimit,
            MaxTemplateSizeChars = maxSizeChars,
            RenderTimeoutMilliseconds = timeoutMs,
            MaxOutputChars = maxOutputChars,
        }), new ScribanParseCache());

    private static JsonElement Variables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static Task<Result<string>> WrapAsync(
        ScribanTemplateEngine engine,
        ScribanTemplateEngine.FormRenderScope scope,
        string layoutSource,
        string rendered)
        => engine.RenderContentAsync(
            scope,
            layoutSource,
            LayoutValidation.ContentPlaceholderVariable,
            rendered,
            CancellationToken.None);

    [Fact]
    public async Task A_layout_rendered_after_a_field_is_refused_the_payload_of_that_field()
    {
        ScribanTemplateEngine engine = Engine();
        ScribanTemplateEngine.FormRenderScope scope = engine.BeginForm();
        Result<string> body = await engine.RenderAsync(
            scope,
            "<p>{{ cpf }}</p>",
            Variables($$"""{ "cpf": "{{SensitiveValue}}" }"""),
            CancellationToken.None);
        body.IsSuccess.ShouldBeTrue(body.Error);

        Result<string> wrapped = await WrapAsync(
            engine, scope, "<html>{{ content }}<!-- {{ cpf }} --></html>", body.Value!);

        // The refusal is the assertion, not the absence of the value. A layout
        // that reads a template variable has to fail closed; asserting only
        // that the value does not appear would also pass for an engine that
        // resolved the variable to nothing, which is the same leak with an
        // empty payload.
        wrapped.IsFailure.ShouldBeTrue();
        wrapped.Error!.ShouldContain("cpf");
        wrapped.Error!.ShouldContain("was not found");
        wrapped.Error!.ShouldNotContain(SensitiveValue);
    }

    [Fact]
    public async Task A_layout_failure_never_echoes_the_body_it_was_framing()
    {
        // The finished body is the recipient's own data by the time it reaches
        // the layout, and an engine diagnostic that quotes the offending value
        // travels out as problem detail.
        ScribanTemplateEngine engine = Engine();
        ScribanTemplateEngine.FormRenderScope scope = engine.BeginForm();
        const string RenderedBody = "<p>Ana deve 4242</p>";

        Result<string> wrapped = await WrapAsync(engine, scope, "{{ math.abs content }}", RenderedBody);

        wrapped.IsFailure.ShouldBeTrue();
        wrapped.Error!.ShouldNotContain(RenderedBody);
        wrapped.Error!.ShouldContain("***");
    }

    [Fact]
    public async Task A_field_cannot_read_what_an_earlier_field_of_the_same_form_assigned()
    {
        ScribanTemplateEngine engine = Engine();
        ScribanTemplateEngine.FormRenderScope scope = engine.BeginForm();
        Result<string> subject = await engine.RenderAsync(
            scope, "{{ injected = 'leak' }}ok", variables: null, CancellationToken.None);
        Result<string> body = await engine.RenderAsync(
            scope, "{{ injected }}", variables: null, CancellationToken.None);

        subject.IsSuccess.ShouldBeTrue(subject.Error);
        subject.Value.ShouldBe("ok");

        // A top-level assignment writes into the globals the render pushed, so
        // the next field sees an undeclared variable and is refused.
        body.IsFailure.ShouldBeTrue();
        body.Error!.ShouldContain("injected");
        body.Error!.ShouldContain("was not found");
    }

    [Fact]
    public async Task Each_field_of_a_form_renders_into_a_buffer_of_its_own()
    {
        // Each field sits under the ceiling; the two together cross it. One
        // shared sink would both append the second field to the first and fail
        // it on a ceiling that belongs to the field, not to the form.
        ScribanTemplateEngine engine = Engine(maxOutputChars: 40);
        ScribanTemplateEngine.FormRenderScope scope = engine.BeginForm();
        var first = new string('a', 30);
        var second = new string('b', 30);

        Result<string> subject = await engine.RenderAsync(
            scope, first, variables: null, CancellationToken.None);
        Result<string> body = await engine.RenderAsync(
            scope, second, variables: null, CancellationToken.None);

        subject.IsSuccess.ShouldBeTrue(subject.Error);
        subject.Value.ShouldBe(first);
        body.IsSuccess.ShouldBeTrue(body.Error);
        body.Value.ShouldBe(second);
    }

    [Fact]
    public async Task A_field_that_crosses_the_deadline_leaves_the_next_field_its_whole_budget()
    {
        ScribanTemplateEngine engine = Engine(loopLimit: 10_000_000, timeoutMs: 200);
        ScribanTemplateEngine.FormRenderScope scope = engine.BeginForm();

        Result<string> body = await engine.RenderAsync(
            scope, "{{ for i in 1..9000000 }}{{ i }}{{ end }}", variables: null, CancellationToken.None);
        Result<string> bodyText = await engine.RenderAsync(
            scope, "ok", variables: null, CancellationToken.None);

        body.IsFailure.ShouldBeTrue();
        body.Error!.ShouldContain("time limit");

        // One source per form would already be cancelled here, and every
        // remaining field of the notification would come back as a timeout it
        // never caused.
        bodyText.IsSuccess.ShouldBeTrue(bodyText.Error);
        bodyText.Value.ShouldBe("ok");
    }

    [Fact]
    public async Task A_pause_longer_than_the_deadline_does_not_expire_the_next_field()
    {
        ScribanTemplateEngine engine = Engine(timeoutMs: 200);
        ScribanTemplateEngine.FormRenderScope scope = engine.BeginForm();

        Result<string> subject = await engine.RenderAsync(
            scope, "ok", variables: null, CancellationToken.None);

        // Whatever happens between two fields of a form, layout resolution or
        // masking included, spends no part of the next field's budget. The
        // pause is three times the limit, so a deadline that covered the form
        // would be long expired.
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        Result<string> body = await engine.RenderAsync(
            scope, "ainda vale", variables: null, CancellationToken.None);

        subject.IsSuccess.ShouldBeTrue(subject.Error);
        body.IsSuccess.ShouldBeTrue(body.Error);
        body.Value.ShouldBe("ainda vale");
    }

    [Fact]
    public async Task The_loop_budget_starts_over_for_every_field_of_a_form()
    {
        // Three fields, each spending more than a third of the limit: a budget
        // that carried across the form would stop the second one.
        ScribanTemplateEngine engine = Engine(loopLimit: 100);
        ScribanTemplateEngine.FormRenderScope scope = engine.BeginForm();
        const string Source = "{{ for i in 1..60 }}x{{ end }}";
        var expected = new string('x', 60);

        Result<string> subject = await engine.RenderAsync(
            scope, Source, variables: null, CancellationToken.None);
        Result<string> body = await engine.RenderAsync(
            scope, Source, variables: null, CancellationToken.None);
        Result<string> bodyText = await engine.RenderAsync(
            scope, Source, variables: null, CancellationToken.None);

        subject.Value.ShouldBe(expected);
        body.IsSuccess.ShouldBeTrue(body.Error);
        body.Value.ShouldBe(expected);
        bodyText.IsSuccess.ShouldBeTrue(bodyText.Error);
        bodyText.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task A_fixed_form_renders_the_same_bytes_on_a_shared_context_and_on_one_per_render()
    {
        ScribanTemplateEngine engine = Engine();

        (var shared, var wrappedBody, var wrappedBodyText) = await SharedContextFormAsync(engine);
        var separate = await SeparateContextFormAsync(engine);

        // The frozen value was taken before the context became shared. It is
        // the whole net of the change: subject, body, text variant, layout
        // frames, loop output and accents all reach the hash, so any drift in
        // the rendered bytes moves it.
        shared.ShouldBe("efd55733d60e050a88eeeed0a45c09522005e536fc9e97f96e9efb5d6a228b36");
        separate.ShouldBe(shared);

        // Named so a failure says which bytes moved instead of only that a
        // digest did.
        wrappedBody.ShouldBe(
            "<html><header>MB</header><p>Olá Ana, o pedido 42 tem 2 itens.</p>"
            + "<ul><li>Café: 2</li><li>Chá: 1</li></ul><footer>rodapé</footer></html>");
        wrappedBodyText.ShouldBe("MB\nOlá Ana, o pedido 42 foi atualizado.\nrodapé");
    }

    /// <summary>The form as the published renderer runs it: one context, five renders.</summary>
    private static async Task<(string Hash, string Body, string BodyText)> SharedContextFormAsync(
        ScribanTemplateEngine engine)
    {
        ScribanTemplateEngine.FormRenderScope scope = engine.BeginForm();
        JsonElement payload = Variables(Payload);
        Result<string> subject = await engine.RenderAsync(
            scope, SubjectSource, payload, CancellationToken.None);
        Result<string> body = await engine.RenderAsync(scope, BodySource, payload, CancellationToken.None);
        Result<string> bodyText = await engine.RenderAsync(
            scope, BodyTextSource, payload, CancellationToken.None);
        Result<string> wrapped = await WrapAsync(engine, scope, LayoutBodySource, body.Value!);
        Result<string> wrappedText = await WrapAsync(engine, scope, LayoutBodyTextSource, bodyText.Value!);

        return (
            CanonicalHash.OfFields(subject.Value, wrapped.Value, wrappedText.Value),
            wrapped.Value!,
            wrappedText.Value!);
    }

    /// <summary>The same form through the entry point that builds a context per render.</summary>
    private static async Task<string> SeparateContextFormAsync(ScribanTemplateEngine engine)
    {
        JsonElement payload = Variables(Payload);
        Result<string> subject = await engine.RenderAsync(SubjectSource, payload, CancellationToken.None);
        Result<string> body = await engine.RenderAsync(BodySource, payload, CancellationToken.None);
        Result<string> bodyText = await engine.RenderAsync(BodyTextSource, payload, CancellationToken.None);

        // The layout frames go through the payload entry point, on globals
        // serialized the way the wrapper built them before it stopped paying
        // for JSON: same bytes out, from the other side of the change.
        Result<string> wrapped = await engine.RenderAsync(
            LayoutBodySource, SerializedContent(body.Value!), CancellationToken.None);
        Result<string> wrappedText = await engine.RenderAsync(
            LayoutBodyTextSource, SerializedContent(bodyText.Value!), CancellationToken.None);

        return CanonicalHash.OfFields(subject.Value, wrapped.Value, wrappedText.Value);
    }

    private static JsonElement SerializedContent(string rendered)
        => JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            [LayoutValidation.ContentPlaceholderVariable] = rendered,
        });
}
