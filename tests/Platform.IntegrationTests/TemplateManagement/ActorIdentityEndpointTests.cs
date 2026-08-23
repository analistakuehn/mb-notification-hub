using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class ActorIdentityEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task The_audited_actor_is_the_token_object_id_when_oid_and_sub_differ()
    {
        HttpClient client = fixture.CreateClientWithObjectId(
            "oid-actor-1",
            "sub-actor-1",
            AuthorizationSetup.AuthorRole);
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());

        await fixture.ExecuteDbAsync(async db =>
        {
            AuditEvent audit = await db.AuditEvents
                .AsNoTracking()
                .SingleAsync(candidate => candidate.EntityId == key);
            audit.ActorId.ShouldBe("oid-actor-1");
        });
    }
}
