using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.AttachmentManagement;

public sealed class AttachmentObjectLocatorTests
{
    private const string Store = "custody-store";
    private const string Key = "attachments/6b4c1f7a";
    private const string Version = "AbCdEf0123456789";

    [Fact]
    public void A_complete_triple_is_accepted_and_kept_exactly_as_given()
    {
        Result<AttachmentObjectLocator> locator = AttachmentObjectLocator.Create(
            Store,
            Key,
            Version);

        locator.IsSuccess.ShouldBeTrue();
        AttachmentObjectLocator value = locator.Value.ShouldNotBeNull();
        value.Store.ShouldBe(Store);
        value.Key.ShouldBe(Key);
        value.Version.ShouldBe(Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("NULL")]
    public void A_generation_the_store_did_not_name_is_refused(string? version)
    {
        Result<AttachmentObjectLocator> locator = AttachmentObjectLocator.Create(
            Store,
            Key,
            version);

        locator.IsFailure.ShouldBeTrue();
        locator.Error.ShouldBe(ErrorCodes.StoreUnavailable);
        locator.Value.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_store_or_key_is_refused(string? absent)
    {
        AttachmentObjectLocator.Create(absent, Key, Version).IsFailure.ShouldBeTrue();
        AttachmentObjectLocator.Create(Store, absent, Version).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_value_longer_than_the_documented_ceiling_is_refused()
    {
        AttachmentObjectLocator.Create(
            new string('s', AttachmentObjectLocator.MaxStoreLength + 1),
            Key,
            Version).IsFailure.ShouldBeTrue();
        AttachmentObjectLocator.Create(
            Store,
            new string('k', AttachmentObjectLocator.MaxKeyLength + 1),
            Version).IsFailure.ShouldBeTrue();
        AttachmentObjectLocator.Create(
            Store,
            Key,
            new string('v', AttachmentObjectLocator.MaxVersionLength + 1)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_value_at_the_documented_ceiling_is_accepted()
        => AttachmentObjectLocator.Create(
            new string('s', AttachmentObjectLocator.MaxStoreLength),
            new string('k', AttachmentObjectLocator.MaxKeyLength),
            new string('v', AttachmentObjectLocator.MaxVersionLength))
            .IsSuccess
            .ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("NULL")]
    public void A_stored_row_that_does_not_name_a_generation_is_refused_when_it_is_read_back(
        string version)
    {
        InvalidOperationException rejection = Should.Throw<InvalidOperationException>(
            () => AttachmentObjectLocator.FromStoredRow(Store, Key, version));

        rejection.Message.ShouldNotContain(Store);
        rejection.Message.ShouldNotContain(Key);
    }

    [Fact]
    public void A_stored_row_that_does_not_name_a_store_or_a_key_is_refused_when_it_is_read_back()
    {
        Should.Throw<InvalidOperationException>(
            () => AttachmentObjectLocator.FromStoredRow(string.Empty, Key, Version));
        Should.Throw<InvalidOperationException>(
            () => AttachmentObjectLocator.FromStoredRow(Store, "   ", Version));
        Should.Throw<InvalidOperationException>(() => AttachmentObjectLocator.FromStoredRow(
            Store,
            Key,
            new string('v', AttachmentObjectLocator.MaxVersionLength + 1)));
    }

    [Fact]
    public void A_complete_stored_row_is_rehydrated_exactly_as_stored()
    {
        AttachmentObjectLocator locator = AttachmentObjectLocator.FromStoredRow(Store, Key, Version);

        locator.Store.ShouldBe(Store);
        locator.Key.ShouldBe(Key);
        locator.Version.ShouldBe(Version);
    }

    [Fact]
    public void Rendering_a_locator_as_text_reveals_no_storage_coordinate()
    {
        AttachmentObjectLocator locator = AttachmentObjectLocator.FromStoredRow(Store, Key, Version);

        var rendered = $"{locator}";

        rendered.ShouldBe(AttachmentObjectLocator.Redacted);
        rendered.ShouldNotContain(Store);
        rendered.ShouldNotContain(Key);
        rendered.ShouldNotContain(Version);
    }
}
