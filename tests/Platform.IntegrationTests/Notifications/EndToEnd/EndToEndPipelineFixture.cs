using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace NotificationHub.IntegrationTests.Notifications.EndToEnd;

/// <summary>
/// The Core pipeline environment on stores of its own, plus the account token
/// the API host verifies Twilio callbacks with.
/// <para>
/// Stores of its own because the suites here drive the scheduler, and the
/// scheduler's scans read the whole table: every attempt still waiting on a
/// deadline, every notification still parked. Run against the shared pipeline
/// database, a scan whose clock stands past a deadline would claim the rows of
/// whatever test ran before, ask the Core for their next plan step and hand a
/// neighbour a second message to a person it never meant to write to. The
/// isolation is what lets a scan here be asserted by the exact count of rows it
/// claimed.
/// </para>
/// <para>
/// The callback token lives here rather than in the shared environment because
/// it is what turns this host into a receiver of provider feedback, and only
/// the scenarios that close a notification from the outside need that.
/// </para>
/// </summary>
public sealed class EndToEndPipelineFixture : CorePipelineFixture
{
    /// <summary>Twilio account auth token this environment signs its callback vectors with.</summary>
    public const string TwilioAuthToken = "end-to-end-twilio-auth-token";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration)
            => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Dispatch:Webhooks:Twilio:AuthToken"] = TwilioAuthToken,
            }));
    }
}

[CollectionDefinition(Name)]
public sealed class EndToEndPipelineCollectionDefinition : ICollectionFixture<EndToEndPipelineFixture>
{
    public const string Name = "end-to-end-pipeline";
}
