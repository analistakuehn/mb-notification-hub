namespace NotificationHub.PerformanceTests.ProviderTransfer;

/// <summary>The three places a transfer can be slowed down or stopped.</summary>
internal enum TransferStage
{
    /// <summary>Reading the attachment bytes from the source.</summary>
    SourceRead,

    /// <summary>Turning those bytes into the base64 the body carries.</summary>
    Encode,

    /// <summary>Pushing the body onto the connection.</summary>
    HttpWrite,
}

/// <summary>What the injection does when it reaches its trigger.</summary>
internal enum TransferReaction
{
    /// <summary>Holds the stage still, which is backpressure and nothing else.</summary>
    Delay,

    /// <summary>Cancels the operation cooperatively.</summary>
    Cancel,

    /// <summary>Fails the stage the way a broken read or a broken socket would.</summary>
    Fault,
}

/// <summary>
/// One injection: at which stage, after how many bytes of that stage, and what
/// it does. A repeating delay is sustained backpressure; a single one is a
/// stall.
/// </summary>
internal sealed record TransferInterruption(
    TransferStage Stage,
    long AfterBytes,
    TransferReaction Reaction,
    TimeSpan Delay = default,
    bool Repeat = false)
{
    internal static TransferInterruption CancelAt(TransferStage stage, long afterBytes)
        => new(stage, afterBytes, TransferReaction.Cancel);

    internal static TransferInterruption FaultAt(TransferStage stage, long afterBytes)
        => new(stage, afterBytes, TransferReaction.Fault);

    internal static TransferInterruption BackpressureFrom(TransferStage stage, long afterBytes, TimeSpan delay)
        => new(stage, afterBytes, TransferReaction.Delay, delay, Repeat: true);
}

/// <summary>
/// Carries the injections through the pipeline and owns the token every arm
/// runs under. The token is linked to the run token, so a cancellation the
/// injection raises and a cancellation the operator raises reach the arm by
/// the same path, and neither is a special case inside it.
/// </summary>
internal sealed class TransferInterrupter : IDisposable
{
    private readonly IReadOnlyList<TransferInterruption> _interruptions;
    private readonly CancellationTokenSource _cancellation;
    private readonly bool[] _fired;

    private TransferInterrupter(
        IReadOnlyList<TransferInterruption> interruptions,
        CancellationTokenSource cancellation)
    {
        _interruptions = interruptions;
        _cancellation = cancellation;
        _fired = new bool[interruptions.Count];
    }

    /// <summary>The token every stage of the arm observes.</summary>
    internal CancellationToken Token => _cancellation.Token;

    internal TransferStage? CancelledAt { get; private set; }

    internal static TransferInterrupter Idle(CancellationToken runToken)
        => new([], CancellationTokenSource.CreateLinkedTokenSource(runToken));

    internal static TransferInterrupter With(
        CancellationToken runToken,
        params TransferInterruption[] interruptions)
    {
        ArgumentNullException.ThrowIfNull(interruptions);
        return new TransferInterrupter(
            [.. interruptions],
            CancellationTokenSource.CreateLinkedTokenSource(runToken));
    }

    public void Dispose() => _cancellation.Dispose();

    /// <summary>
    /// Called by every stage after each unit of work, with the bytes that
    /// stage has handled so far. Doing nothing is the whole cost when no
    /// injection is armed.
    /// </summary>
    internal async ValueTask ObserveAsync(
        TransferStage stage,
        long stageBytes,
        CancellationToken cancellationToken)
    {
        if (_interruptions.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        for (var index = 0; index < _interruptions.Count; index++)
        {
            TransferInterruption interruption = _interruptions[index];
            if (interruption.Stage != stage || stageBytes < interruption.AfterBytes)
            {
                continue;
            }

            if (_fired[index] && !interruption.Repeat)
            {
                continue;
            }

            _fired[index] = true;
            switch (interruption.Reaction)
            {
                case TransferReaction.Delay:
                    await Task.Delay(interruption.Delay, cancellationToken);
                    break;
                case TransferReaction.Cancel:
                    CancelledAt = stage;
                    await _cancellation.CancelAsync();
                    break;
                case TransferReaction.Fault:
                    throw new IOException(
                        $"Falha injetada no estágio {stage} após {stageBytes} bytes.");
                default:
                    throw new InvalidOperationException(
                        $"Reação de interrupção desconhecida: {interruption.Reaction}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
