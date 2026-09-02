namespace NotificationHub.IntegrationTests.ProviderTransfer;

/// <summary>
/// The provider-transfer tests run alone, and the reason is a property of the
/// instrument rather than a preference. Allocation per operation is read from
/// the whole process, so an arm measured while hundreds of other tests run at
/// the same time is charged their work as well: measured in parallel, the
/// incremental arm of a 256 KiB corpus reported anywhere between 336 KB and
/// 5,3 MB per send, against about 20 KB when it is the only thing running.
/// <para>
/// The same limitation is why the probe is a program of its own. A number this
/// gate compares only means something in a process that is doing nothing else.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProviderTransferMeasurementCollectionDefinition
{
    internal const string Name = "provider-transfer-measurement";
}
