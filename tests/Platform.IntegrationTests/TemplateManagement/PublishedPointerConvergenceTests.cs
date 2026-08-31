using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// The convergence the published read surface promises to a process that did
/// not run the command: it keeps answering the previous value until its own
/// pointer window closes, and it converges once that window closes. Both
/// halves are asserted, because a surface with no memoization at all satisfies
/// the second one alone.
/// </summary>
/// <remarks>
/// The second process is a host derived over the same store, holding a clock
/// this test owns, so the window can close without a minute of sleeping. Its
/// memoization is a distinct instance because the container that owns it is,
/// which is what makes it stand for a process that never saw the command.
/// </remarks>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class PublishedPointerConvergenceTests(TemplateManagementApiFixture fixture)
{
    private const string Application = "araia-cambio";

    [RequiresDockerFact]
    public async Task A_process_that_did_not_run_the_disable_keeps_answering_until_its_window_closes()
    {
        var reader = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using WebApplicationFactory<Program> other = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(reader)));

        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, version);

        // Both processes memoize the pointer before the command runs: the one
        // that commits it, so its convergence proves the invalidation ran and
        // not a cold load; the one that does not, so its window is what the
        // assertions below measure.
        (await FindTemplateAsync(fixture, key)).Value.ShouldBeOfType<PublishedTemplateLookup.Published>();
        (await FindTemplateAsync(other, key)).Value.ShouldBeOfType<PublishedTemplateLookup.Published>();

        HttpResponseMessage disabled = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/disable",
            new { reason = "content-incorrect", note = "conteúdo incorreto em produção" });
        disabled.EnsureSuccessStatusCode();

        Result<PublishedTemplateLookup> committer = await FindTemplateAsync(fixture, key);
        reader.Advance(TimeSpan.FromSeconds(59));
        Result<PublishedTemplateLookup> inside = await FindTemplateAsync(other, key);
        reader.Advance(TimeSpan.FromSeconds(2));
        Result<PublishedTemplateLookup> beyond = await FindTemplateAsync(other, key);

        inside.Value.ShouldBeOfType<PublishedTemplateLookup.Published>(
            "o processo que não rodou o comando parou de responder o valor anterior antes do fim "
            + "da janela do ponteiro, e a fronteira publicada promete que isso nunca acontece antes");
        beyond.Value.ShouldBeOfType<PublishedTemplateLookup.Rejected>(
            "a janela do ponteiro fechou e o processo que não rodou o comando continuou respondendo "
            + "o valor anterior, então nada o faz convergir")
            .Reason.ShouldBe(TemplateRejectionReasons.Disabled);

        // Without this the staleness above could be a command that never
        // landed, and the two assertions would hold for a store nobody wrote.
        committer.Value.ShouldBeOfType<PublishedTemplateLookup.Rejected>(
            "o processo que rodou o comando não convergiu na hora, então a invalidação local "
            + "não acompanhou o commit")
            .Reason.ShouldBe(TemplateRejectionReasons.Disabled);
    }

    private static async Task<Result<PublishedTemplateLookup>> FindTemplateAsync(
        WebApplicationFactory<Program> host,
        string key)
    {
        using IServiceScope scope = host.Services.CreateScope();
        IPublishedCatalog catalog = scope.ServiceProvider.GetRequiredService<IPublishedCatalog>();
        return await catalog.FindTemplateAsync(Application, key, CancellationToken.None);
    }
}
