using System.Globalization;
using System.Text.Json;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;

/// <summary>
/// Second policy rule: the silence window, driven entirely by the published
/// data. A null window allows with evidence; an active window defers to its
/// end in the recipient's timezone. A hard guard in code, not in data, keeps
/// critical and authentication flows out of any deferral: whatever a policy
/// says, an OTP never waits for the morning.
/// </summary>
internal sealed class QuietHoursRule(TimeProvider timeProvider) : IPolicyRule<NotificationContext>
{
    internal const string RuleName = "QuietHours";
    internal const string ReasonQuietHours = "quiet-hours";

    public string Name => RuleName;

    public Task<PolicyRuleResult> EvaluateAsync(
        NotificationContext context,
        ClassPolicyDefinition policy,
        CancellationToken cancellationToken)
    {
        if (ResolveStage.IsCriticalOrAuthentication(context))
        {
            return Task.FromResult<PolicyRuleResult>(new PolicyRuleResult.Allow
            {
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    guard = "critical-or-authentication-never-deferred",
                    window = Describe(policy.QuietHours),
                }),
            });
        }

        if (policy.QuietHours is not { } window)
        {
            return Task.FromResult<PolicyRuleResult>(new PolicyRuleResult.Allow
            {
                EvidenceJson = JsonSerializer.Serialize(new { window = (string?)null }),
            });
        }

        RecipientSnapshot recipient = context.Recipient
            ?? throw new InvalidOperationException("A regra de janela de silêncio requer o destinatário resolvido.");
        (TimeZoneInfo timezone, var resolvedTimezone) = ResolveTimezone(recipient.Timezone);
        DateTimeOffset nowUtc = timeProvider.GetUtcNow();
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(nowUtc, timezone);
        if (NextReleaseInstant(localNow, window, timezone) is not { } releaseAt)
        {
            return Task.FromResult<PolicyRuleResult>(new PolicyRuleResult.Allow
            {
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    window = Describe(window),
                    timezone = resolvedTimezone,
                    declaredTimezone = recipient.Timezone,
                    localTime = localNow.ToString("HH:mm", CultureInfo.InvariantCulture),
                }),
            });
        }

        return Task.FromResult<PolicyRuleResult>(new PolicyRuleResult.Defer(releaseAt)
        {
            EvidenceJson = JsonSerializer.Serialize(new
            {
                window = Describe(window),
                timezone = resolvedTimezone,
                declaredTimezone = recipient.Timezone,
                localTime = localNow.ToString("HH:mm", CultureInfo.InvariantCulture),
                releaseAt,
            }),
        });
    }

    /// <summary>
    /// The timezone the window is measured in, and the identifier that was
    /// actually used. A declared identifier the runtime cannot resolve falls
    /// back to the platform default, which is the same reading the recipient
    /// contract already gives to a recipient who declared nothing: the value is
    /// unusable either way, and this rule has to reach a decision rather than
    /// propagate an exception through the stage that exists to produce one.
    /// <para>
    /// The substitution is named in the evidence next to the declared value, so
    /// a deferral measured in a timezone the recipient did not declare is
    /// visible in the trail instead of being indistinguishable from one that
    /// was. A default the runtime cannot resolve either is not a data problem
    /// but a host without a timezone database, and that is not something this
    /// rule can decide around.
    /// </para>
    /// </summary>
    internal static (TimeZoneInfo Timezone, string Resolved) ResolveTimezone(string declared)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(declared, out TimeZoneInfo? resolved))
        {
            return (resolved, declared);
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(
            RecipientSnapshot.DefaultTimezone, out TimeZoneInfo? platformDefault))
        {
            return (platformDefault, RecipientSnapshot.DefaultTimezone);
        }

        throw new InvalidOperationException(
            $"O fuso padrão da plataforma '{RecipientSnapshot.DefaultTimezone}' não existe neste host; "
            + "a janela de silêncio não pode ser avaliada sem base de fusos.");
    }

    /// <summary>
    /// The instant the window ends, in UTC, when the local time sits inside
    /// the window; null when delivery may proceed now. Windows may wrap
    /// midnight: from 22:00 to 08:00 is silent at 23:00 and at 06:00.
    /// <para>
    /// The end of the window is a wall-clock time, and a daylight-saving
    /// transition can make one of those not exist or happen twice. Both are
    /// resolved on the side that keeps the silence rather than breaks it: the
    /// offset of a repeated hour is the standard one, which picks the later of
    /// the two instants, and the offset of an hour the clock skipped is the one
    /// in force before the jump, which releases at the moment the wall clock
    /// passes the end of the window instead of an hour after it.
    /// </para>
    /// </summary>
    internal static DateTimeOffset? NextReleaseInstant(
        DateTimeOffset localNow,
        QuietHoursWindow window,
        TimeZoneInfo timezone)
    {
        var time = TimeOnly.FromDateTime(localNow.DateTime);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        bool inside;
        DateOnly releaseDate;
        if (window.From <= window.To)
        {
            inside = time >= window.From && time < window.To;
            releaseDate = today;
        }
        else
        {
            inside = time >= window.From || time < window.To;
            releaseDate = time >= window.From ? today.AddDays(1) : today;
        }

        if (!inside)
        {
            return null;
        }

        var releaseLocal = releaseDate.ToDateTime(window.To, DateTimeKind.Unspecified);
        return new DateTimeOffset(releaseLocal, timezone.GetUtcOffset(releaseLocal)).ToUniversalTime();
    }

    private static string? Describe(QuietHoursWindow? window)
        => window is null
            ? null
            : $"{window.From.ToString("HH:mm", CultureInfo.InvariantCulture)}"
                + $"-{window.To.ToString("HH:mm", CultureInfo.InvariantCulture)}";
}
