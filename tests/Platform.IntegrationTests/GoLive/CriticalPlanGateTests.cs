using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.Platform.GoLiveChecks;

namespace NotificationHub.IntegrationTests.GoLive;

/// <summary>
/// The one violation the go-live gate still names, measured against the real
/// governed catalog instead of against the text of its own query.
/// <para>
/// A unit test can only say that the statement mentions the right table and
/// binds its filters. What it cannot say is whether the filters match the
/// values this schema actually stores, and that is precisely the failure that
/// would be silent: a class or a status spelled differently from the stored
/// canonical value returns no rows, and a gate that finds nothing releases the
/// fleet while a critical notification rides on a single channel.
/// </para>
/// <para>
/// Every assertion is a delta against the count taken at the start, never an
/// absolute number. This collection publishes policies of its own throughout,
/// so an absolute count would grade whatever ran before instead of the rows
/// this test writes.
/// </para>
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class CriticalPlanGateTests(CorePipelineFixture fixture)
{
    private static readonly string[] PushOnly = ["push"];
    private static readonly string[] PushAndSms = ["push", "sms"];

    [RequiresDockerFact]
    public async Task Only_a_published_critical_plan_without_a_later_step_is_counted()
    {
        var before = await CountPlansWithoutFallbackAsync();

        await PublishPolicyAsync(NotificationClasses.Critical, WithFallback());
        (await CountPlansWithoutFallbackAsync()).ShouldBe(
            before,
            "um plano crítico com passo posterior tem fallback e não pode reprovar o portão.");

        await PublishPolicyAsync(NotificationClasses.Critical, WithoutFallback());
        (await CountPlansWithoutFallbackAsync()).ShouldBe(
            before + 1,
            "uma política crítica publicada com plano de um passo só é exatamente "
            + "a notificação sem fallback que o critério da fase proíbe.");

        // A draft is not in force, and neither is another class: the gate has
        // to separate what governs traffic today from what is merely written.
        HttpClient author = fixture.CreateAuthorClient("policy-author");
        await ClassPolicyApi.CreateDraftAsync(
            author, ClassPolicyApi.NewApplication(), NotificationClasses.Critical, WithoutFallback());
        (await CountPlansWithoutFallbackAsync()).ShouldBe(
            before + 1, "um rascunho não governa tráfego e não pode reprovar o portão.");

        await PublishPolicyAsync(NotificationClasses.Operational, WithoutFallback());
        (await CountPlansWithoutFallbackAsync()).ShouldBe(
            before + 1,
            "o critério de 100 % com fallback é da classe crítica; outra classe "
            + "com plano de um passo não é violação deste portão.");
    }

    private static object WithFallback()
        => new
        {
            schemaVersion = 1,
            channelsAllowed = PushAndSms,
            deliveryPlan = new object[]
            {
                new { channel = "push", timeout = "30s" },
                new { channel = "sms" },
            },
            defaultTtl = "300s",
            dedupeWindow = "60s",
            quietHours = (object?)null,
            consentPurpose = (string?)null,
        };

    private static object WithoutFallback()
        => new
        {
            schemaVersion = 1,
            channelsAllowed = PushOnly,
            deliveryPlan = new object[] { new { channel = "push" } },
            defaultTtl = "300s",
            dedupeWindow = "60s",
            quietHours = (object?)null,
            consentPurpose = (string?)null,
        };

    private async Task PublishPolicyAsync(string policyClass, object definition)
    {
        HttpClient author = fixture.CreateAuthorClient("policy-author");
        HttpClient publisher = fixture.CreatePublisherClient("policy-publisher");
        var application = ClassPolicyApi.NewApplication();
        await ClassPolicyApi.CreateDraftAsync(author, application, policyClass, definition);
        await ClassPolicyApi.PublishAsync(publisher, application, policyClass);
    }

    /// <summary>
    /// The gate's own source, over the gate's own executor, against the
    /// database this fixture governs. Composing the source here rather than
    /// copying its statement is the whole point: a copy would grade the copy.
    /// </summary>
    private async Task<int> CountPlansWithoutFallbackAsync()
    {
        var source = new CriticalPlanWithoutFallbackSource(
            new NpgsqlCountQueryExecutor(),
            fixture.PostgresConnectionString,
            NotificationClasses.Critical,
            ClassPolicyVersionStatuses.Published);
        GoLiveSourceCheck check = await source.CheckAsync(CancellationToken.None);
        return check.Count;
    }
}
