namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// Serialises the classes that read execution plans against a database of their
/// own.
/// <para>
/// Each of them provisions a container, seeds tens of thousands of rows and then
/// drops and recreates indexes, which is why none of them can share a fixture
/// with anything: a dropped index would break whatever ran next. What they can
/// share is a turn. Left in separate collections they provision their databases
/// at the same time, on top of every other container the suite is already
/// running, and the failure that produces is not a red assertion but a
/// container that never becomes ready, which reads as a broken build.
/// </para>
/// <para>
/// The collection carries no fixture on purpose. Its whole job is the ordering
/// that xUnit gives to classes sharing a collection.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class QueryPlanCollectionDefinition
{
    public const string Name = "query-plan";
}
