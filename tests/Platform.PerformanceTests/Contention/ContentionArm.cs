using NotificationHub.PerformanceTests.Infrastructure;

namespace NotificationHub.PerformanceTests.Contention;

/// <summary>One appender: which partition it writes to and which operations it draws from.</summary>
internal sealed record AppenderSpec(string Label, PartitionMonth Partition, IReadOnlyList<AppendOperation> Mixture);

/// <summary>
/// One arm of the factorial design. Arms differ in exactly one dimension at a
/// time, which is what lets the report attribute a delta to the lock instead of
/// to the database: same rate, same pool, same volume, same statements.
/// </summary>
internal sealed record ContentionArm(
    string Id,
    string Question,
    AppendShape Shape,
    bool RequiresTailIndex,
    IReadOnlyList<AppenderSpec> Appenders);

/// <summary>The append profile the repository's writers actually produce.</summary>
internal static class AppendProfiles
{
    /// <summary>
    /// The bare append, used by the control and the treatment. Keeping the
    /// operation identical on both sides is the whole point of the pair.
    /// </summary>
    internal static IReadOnlyList<AppendOperation> BareAppend { get; } =
        [new AppendOperation("append", 1, 0, 1)];

    /// <summary>
    /// The measured mixture. Three appends per notification (acceptance,
    /// pipeline commit, dispatch verdict) plus the two operations that append
    /// outside a notification's life: the rejection or duplicate, which commits
    /// in a short transaction of its own, and the contact or consent write.
    /// The business statement counts mirror what each writer saves before it
    /// calls the trail.
    /// </summary>
    internal static IReadOnlyList<AppendOperation> RealMixture { get; } =
    [
        new AppendOperation("ingestion-accepted", 30, 3, 1),
        new AppendOperation("pipeline-commit", 30, 4, 1),
        new AppendOperation("dispatch-verdict", 30, 3, 1),
        new AppendOperation("ingestion-rejected", 5, 1, 1),
        new AppendOperation("contact-consent", 5, 3, 1),
    ];

    /// <summary>
    /// The audit surface: two links in one transaction with no business effect
    /// to amortize the window. It is the worst case of the batch, and it is the
    /// only caller that appends twice under one lock acquisition.
    /// </summary>
    internal static IReadOnlyList<AppendOperation> AuditSurface { get; } =
        [new AppendOperation("audit-disclosure", 1, 0, 2)];
}

/// <summary>Builds the five arms over a given appender count.</summary>
internal static class ContentionArms
{
    internal static IReadOnlyList<ContentionArm> Build(
        PartitionMonth current,
        IReadOnlyList<PartitionMonth> distinct,
        int appenders)
    {
        ArgumentNullException.ThrowIfNull(distinct);
        return
        [
            new ContentionArm(
                "A1",
                "custo de banco por append quando o lock nunca disputa",
                AppendShape.Current,
                RequiresTailIndex: false,
                [
                    .. Enumerable.Range(0, appenders).Select(index => new AppenderSpec(
                        $"distinct-{index}", distinct[index], AppendProfiles.BareAppend)),
                ]),
            new ContentionArm(
                "A2",
                "os mesmos appenders na partição corrente, com serialização plena",
                AppendShape.Current,
                RequiresTailIndex: false,
                [
                    .. Enumerable.Range(0, appenders).Select(index => new AppenderSpec(
                        $"same-{index}", current, AppendProfiles.BareAppend)),
                ]),
            new ContentionArm(
                "A3",
                "a mistura real de operações na partição corrente",
                AppendShape.Current,
                RequiresTailIndex: false,
                [
                    .. Enumerable.Range(0, appenders).Select(index => new AppenderSpec(
                        $"mixture-{index}", current, AppendProfiles.RealMixture)),
                ]),
            new ContentionArm(
                "A4",
                "a mistura real com a superfície de auditoria concorrente",
                AppendShape.Current,
                RequiresTailIndex: false,
                [
                    .. Enumerable.Range(0, appenders).Select(index => new AppenderSpec(
                        $"mixture-{index}", current, AppendProfiles.RealMixture)),
                    new AppenderSpec("audit-surface", current, AppendProfiles.AuditSurface),
                ]),
            new ContentionArm(
                "A5",
                "a mistura real com índice de cauda e round trips colapsados",
                AppendShape.Collapsed,
                RequiresTailIndex: true,
                [
                    .. Enumerable.Range(0, appenders).Select(index => new AppenderSpec(
                        $"mixture-{index}", current, AppendProfiles.RealMixture)),
                ]),
        ];
    }
}
