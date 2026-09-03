using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

/// <summary>
/// The way to the bytes of an accepted attachment, exercised against the
/// custody the module actually writes to.
/// <para>
/// The handle is the whole input. What this suite holds is that resolving it
/// yields the generation the record names and nothing else, and that every way
/// of not yielding bytes arrives as the one answer a caller can act on.
/// </para>
/// </summary>
[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class AcceptedAttachmentContentTests(AttachmentManagementApiFixture fixture)
{
    private const string Content = "conteudo-do-anexo-entregue-pela-porta-de-conteudo";

    /// <summary>
    /// The handle opens the bytes that were captured, and the reading proves
    /// it against the digest the module recorded while capturing them: a
    /// stream of the right length carrying other bytes satisfies everything
    /// else.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_handle_of_an_accepted_attachment_opens_the_bytes_that_were_captured()
    {
        AttachmentObjectGeneration generation = await UploadedAsync("content-port-producer");
        var handle = AttachmentContentIdentity.For(generation);

        using AcceptedAttachmentContent opened = await Port().OpenAsync(
            handle, CancellationToken.None);

        opened.Status.ShouldBe(AcceptedAttachmentContentStatus.Opened);
        using var read = new MemoryStream();
        await opened.Stream.ShouldNotBeNull().CopyToAsync(read, CancellationToken.None);
        read.ToArray().ShouldBe(Encoding.UTF8.GetBytes(Content));
        Convert.ToHexString(SHA256.HashData(read.ToArray()))
            .ShouldBe(Convert.ToHexString(generation.Digest));
        read.Length.ShouldBe(generation.LengthBytes);
    }

    /// <summary>
    /// Text this module never minted names no generation, and a handle whose
    /// generation is no longer recorded names one that is gone. Both leave
    /// through the answer that yields nothing, because neither yields bytes
    /// and a caller does the same thing about either.
    /// <para>
    /// A recorded generation is arranged first, and it is what makes the three
    /// refusals mean anything: against an empty record every handle refuses,
    /// including the one that names a generation this module does have, so a
    /// reading that ignored the handle altogether would pass. The one that
    /// opens sits beside the three that do not, in the same arrangement.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_handle_that_names_no_recorded_generation_opens_nothing()
    {
        AttachmentObjectGeneration recorded = await UploadedAsync("content-port-refusals-producer");
        IAcceptedAttachmentContent port = Port();

        using (AcceptedAttachmentContent opened = await port.OpenAsync(
            AttachmentContentIdentity.For(recorded), CancellationToken.None))
        {
            opened.Status.ShouldBe(AcceptedAttachmentContentStatus.Opened);
        }

        foreach (var handle in new[]
        {
            "not-a-handle",
            "att_" + Guid.NewGuid().ToString("N"),
            AttachmentContentIdentity.For(Guid.NewGuid()),
        })
        {
            using AcceptedAttachmentContent opened = await port.OpenAsync(
                handle, CancellationToken.None);

            opened.Status.ShouldBe(AcceptedAttachmentContentStatus.Unavailable);
            opened.Stream.ShouldBeNull();
        }
    }

    private IAcceptedAttachmentContent Port()
        => fixture.Services.GetRequiredService<IAcceptedAttachmentContent>();

    /// <summary>
    /// One attachment through the module's own endpoints, so the object and
    /// the generation row are the ones production writes rather than rows a
    /// test composed.
    /// </summary>
    private async Task<AttachmentObjectGeneration> UploadedAsync(string producer)
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, producer);
        using HttpClient client = fixture.CreateProducerClient(producer);
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, Content.Length);
        using (registration)
        {
            using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
                client, registered.Reference, Content);
            upload.EnsureSuccessStatusCode();
        }

        Attachment attachment = await fixture.QueryAttachmentAsync(registered.Reference);
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .ObjectGenerations
            .AsNoTracking()
            .SingleAsync(row => row.AttachmentId == attachment.Id);
    }
}
