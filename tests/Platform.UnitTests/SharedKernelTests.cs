using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests;

public sealed class SharedKernelTests
{
    [Fact]
    public void Validation_error_preserves_the_expected_error_axis()
    {
        var result = Result.ValidationError<int>("invalid input");

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }
}
