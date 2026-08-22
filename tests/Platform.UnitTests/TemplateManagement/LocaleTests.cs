using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class LocaleTests
{
    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("PT-br", "pt-BR")]
    [InlineData("pt", "pt")]
    [InlineData("EN", "en")]
    [InlineData(" es-AR ", "es-AR")]
    public void Normalizes_language_and_region_casing(string input, string expected)
    {
        Result<Locale> result = Locale.Create(input);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ptbr")]
    [InlineData("pt-BRA")]
    [InlineData("p")]
    [InlineData("pt_BR")]
    [InlineData("123")]
    public void Rejects_unsupported_language_tags(string input)
    {
        Result<Locale> result = Locale.Create(input);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Fact]
    public void Locales_with_the_same_normalized_value_are_equal()
    {
        Locale first = Locale.Create("pt-br").Value!;
        Locale second = Locale.Create("PT-BR").Value!;

        first.ShouldBe(second);
    }
}
