using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class NotificationClassTests
{
    [Theory]
    [InlineData("critical", NotificationClass.Critical)]
    [InlineData("transactional", NotificationClass.Transactional)]
    [InlineData("operational", NotificationClass.Operational)]
    public void Accepts_every_supported_class(string value, NotificationClass expected)
    {
        Result<NotificationClass> result = NotificationClasses.Create(value);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expected);
        result.Value.Canonical().ShouldBe(value);
    }

    [Theory]
    [InlineData("marketing")]
    [InlineData("")]
    [InlineData("CRITICAL")]
    public void Rejects_classes_outside_the_supported_set(string value)
    {
        Result<NotificationClass> result = NotificationClasses.Create(value);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }
}
