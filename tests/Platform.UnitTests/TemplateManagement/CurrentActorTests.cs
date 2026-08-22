using System.Security.Claims;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class CurrentActorTests
{
    [Fact]
    public void Prefers_the_object_id_claim_over_the_subject()
    {
        ClaimsPrincipal principal = PrincipalWith(new Claim("oid", "oid-1"), new Claim("sub", "sub-1"));

        Result<string> actor = CurrentActor.Identify(principal);

        actor.IsSuccess.ShouldBeTrue();
        actor.Value.ShouldBe("oid-1");
    }

    [Fact]
    public void Falls_back_to_the_subject_claim()
    {
        ClaimsPrincipal principal = PrincipalWith(new Claim("sub", "sub-1"));

        CurrentActor.Identify(principal).Value.ShouldBe("sub-1");
    }

    [Fact]
    public void Falls_back_to_the_mapped_name_identifier_claim()
    {
        ClaimsPrincipal principal = PrincipalWith(new Claim(ClaimTypes.NameIdentifier, "mapped-1"));

        CurrentActor.Identify(principal).Value.ShouldBe("mapped-1");
    }

    [Fact]
    public void A_token_without_identity_claims_is_forbidden()
    {
        ClaimsPrincipal principal = PrincipalWith(new Claim("name", "someone"));

        Result<string> actor = CurrentActor.Identify(principal);

        actor.IsFailure.ShouldBeTrue();
        actor.ErrorKind.ShouldBe(ResultErrorKind.Forbidden);
        DomainError.Describe(actor.Error, actor.ErrorKind).Code.ShouldBe(ErrorCodes.ActorUnidentified);
    }

    private static ClaimsPrincipal PrincipalWith(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "test"));
}
