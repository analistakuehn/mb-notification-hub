using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests;

/// <summary>
/// The guard that keeps a run from reporting success over tests it never ran.
/// <para>
/// Every scenario that proves a delivery criterion needs Postgres, and every
/// one of those is decorated to skip when the Docker daemon does not answer. A
/// skipped test passes, so a machine with no daemon, or a pipeline whose daemon
/// silently stopped, produces exactly the same green report as a machine that
/// executed all of them. That is the failure this file exists to make loud, and
/// it has to be a test of its own: an assertion inside a skipped test is
/// skipped with it.
/// </para>
/// <para>
/// Ordinary runs are untouched. Without the variable this passes on any
/// machine, which is deliberate: turning a missing daemon into a failure for
/// everybody would make the workstation suite red for a reason nobody asked it
/// to care about. The run that grades a release is the one that sets it.
/// </para>
/// </summary>
public sealed class DockerRequirementGuardTests
{
    [Fact]
    public void A_run_that_requires_docker_refuses_to_report_success_without_it()
    {
        if (!DockerEnvironment.IsRequired)
        {
            // Not the grading run. The scenarios that need a daemon skip with a
            // reason of their own, which is the honest answer here.
            return;
        }

        DockerEnvironment.IsAvailable.ShouldBeTrue(DockerEnvironment.MissingDaemonFailure);
    }
}
