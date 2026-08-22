using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class EntityTagsTests
{
    [Theory]
    [InlineData("\"abc123\"")]
    [InlineData("abc123")]
    [InlineData("W/\"abc123\"")]
    [InlineData("  \"abc123\"  ")]
    [InlineData("*")]
    public void Accepts_the_current_tag_in_every_transported_form(string ifMatch)
    {
        Result result = EntityTags.CheckIfMatch(ifMatch, "abc123");

        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"stale\"")]
    public void Rejects_absent_or_stale_tags_as_a_failed_precondition(string? ifMatch)
    {
        Result result = EntityTags.CheckIfMatch(ifMatch, "abc123");

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.PreconditionFailed);
    }

    [Fact]
    public void The_header_value_is_a_quoted_strong_tag()
    {
        EntityTags.ToHeaderValue("abc123").ShouldBe("\"abc123\"");
    }
}
