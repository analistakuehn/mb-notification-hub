using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.AttachmentManagement;

public sealed class ProducerApplicationGrantTests
{
    [Fact]
    public void Principal_resolution_prefers_oid_then_sub_then_name_identifier()
    {
        ClaimsPrincipal principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, "name-id", null, "issuer-a"),
            new Claim("sub", "subject-id", null, "issuer-b"),
            new Claim("oid", "object-id", null, "issuer-c"));

        AttachmentPrincipal resolved = AttachmentPrincipal.Resolve(principal)
            .ShouldNotBeNull();

        resolved.Issuer.ShouldBe("issuer-c");
        resolved.ClaimKind.ShouldBe("oid");
        resolved.PrincipalId.ShouldBe("object-id");
    }

    [Fact]
    public void Application_and_appid_claims_never_supply_attachment_identity()
    {
        ClaimsPrincipal principal = Principal(
            new Claim("appid", "application-client-id", null, "issuer-a"),
            new Claim("application", "billing-app", null, "issuer-a"));

        AttachmentPrincipal.Resolve(principal).ShouldBeNull();
    }

    [Fact]
    public void Claim_without_an_explicit_issuer_is_denied()
    {
        ClaimsPrincipal principal = Principal(new Claim("sub", "principal"));

        AttachmentPrincipal.Resolve(principal).ShouldBeNull();
    }

    [Theory]
    [InlineData(" ", "sub", "principal")]
    [InlineData("issuer", "sub", "")]
    [InlineData("issuer", "appid", "principal")]
    public void Missing_issuer_identity_or_supported_claim_kind_is_denied(
        string issuer,
        string claimKind,
        string principalId)
    {
        ClaimsPrincipal principal = Principal(new Claim(
            claimKind,
            principalId,
            null,
            issuer));

        AttachmentPrincipal.Resolve(principal).ShouldBeNull();
    }

    [Theory]
    [InlineData("oid")]
    [InlineData("sub")]
    [InlineData(ClaimTypes.NameIdentifier)]
    public void Grant_preserves_the_exact_four_part_key(string claimKind)
    {
        Result<ProducerApplicationGrant> result = ProducerApplicationGrant.Create(
            "issuer-exact",
            claimKind,
            "principal-exact",
            "application-exact");

        ProducerApplicationGrant grant = result.Value.ShouldNotBeNull();
        grant.Issuer.ShouldBe("issuer-exact");
        grant.ClaimKind.ShouldBe(claimKind);
        grant.PrincipalId.ShouldBe("principal-exact");
        grant.Application.ShouldBe("application-exact");
    }

    [Fact]
    public void Rate_limit_partition_uses_the_canonical_identity_tuple_and_address_fallback()
    {
        AttachmentRateLimitPartition issuerA = RateLimitKey(
            Principal(new Claim("sub", "shared-id", null, "issuer-a")),
            "192.0.2.1");
        AttachmentRateLimitPartition issuerB = RateLimitKey(
            Principal(new Claim("sub", "shared-id", null, "issuer-b")),
            "192.0.2.1");
        AttachmentRateLimitPartition subject = RateLimitKey(
            Principal(new Claim("sub", "shared-id", null, "issuer-a")),
            "192.0.2.1");
        AttachmentRateLimitPartition objectId = RateLimitKey(
            Principal(new Claim("oid", "shared-id", null, "issuer-a")),
            "192.0.2.1");
        AttachmentRateLimitPartition anonymous = RateLimitKey(
            new ClaimsPrincipal(),
            "192.0.2.1");
        AttachmentRateLimitPartition nonCanonical = RateLimitKey(
            Principal(new Claim("sub", "shared-id")),
            "192.0.2.1");
        AttachmentRateLimitPartition otherAddress = RateLimitKey(
            new ClaimsPrincipal(),
            "192.0.2.2");

        issuerA.ShouldNotBe(issuerB);
        subject.ShouldNotBe(objectId);
        nonCanonical.ShouldBe(anonymous);
        anonymous.ShouldNotBe(otherAddress);
        subject.ToString().ShouldBe("principal");
        anonymous.ToString().ShouldBe("address");
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "test"));

    private static AttachmentRateLimitPartition RateLimitKey(
        ClaimsPrincipal principal,
        string address)
    {
        var httpContext = new DefaultHttpContext { User = principal };
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return RateLimitingSetup.PartitionKey(httpContext);
    }
}
