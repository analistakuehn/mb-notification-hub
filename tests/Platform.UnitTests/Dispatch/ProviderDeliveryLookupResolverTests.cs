using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.Dispatch;

/// <summary>
/// Which providers can be asked after the fact, and what the absence of a
/// lookup means. The absence is the interesting half: it is how the hub states
/// that a channel is settled by fallback and validity alone, and a resolver
/// that answered "nothing found" instead would make an unanswerable attempt
/// look like an attempt the provider denies.
/// </summary>
public sealed class ProviderDeliveryLookupResolverTests
{
    [Fact]
    public void Resolves_the_lookup_registered_under_each_provider_key()
    {
        ProviderDeliveryLookupResolver resolver = new(
            [new FakeLookup("twilio"), new FakeLookup("sendgrid")]);

        Result<IProviderDeliveryLookup> twilio = resolver.Resolve("twilio");
        Result<IProviderDeliveryLookup> sendgrid = resolver.Resolve("sendgrid");

        twilio.IsSuccess.ShouldBeTrue(twilio.Error);
        twilio.Value!.ProviderKey.ShouldBe("twilio");
        sendgrid.IsSuccess.ShouldBeTrue(sendgrid.Error);
        sendgrid.Value!.ProviderKey.ShouldBe("sendgrid");
    }

    [Fact]
    public void Refuses_a_provider_that_offers_no_lookup_after_the_send()
    {
        // The push provider registers none, which is the whole mechanism: the
        // hub declares the absence by hosting nothing rather than by carrying
        // a list of providers to skip.
        ProviderDeliveryLookupResolver resolver = new(
            [new FakeLookup("twilio"), new FakeLookup("sendgrid")]);

        Result<IProviderDeliveryLookup> result = resolver.Resolve("fcm");

        ProviderLookupRefusal.Is(result, ProviderLookupRefusal.LookupUnsupported)
            .ShouldBeTrue(result.Error);
        result.ErrorKind.ShouldBe(ResultErrorKind.NotFound);
    }

    [Fact]
    public void Refuses_a_key_that_differs_only_by_case_because_matching_is_ordinal()
    {
        ProviderDeliveryLookupResolver resolver = new([new FakeLookup("twilio")]);

        Result<IProviderDeliveryLookup> result = resolver.Resolve("Twilio");

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Refuses_an_empty_key_as_a_provider_nobody_speaks_for()
    {
        ProviderDeliveryLookupResolver resolver = new([new FakeLookup("twilio")]);

        Result<IProviderDeliveryLookup> result = resolver.Resolve("   ");

        ProviderLookupRefusal.Is(result, ProviderLookupRefusal.ProviderUnknown)
            .ShouldBeTrue(result.Error);
    }

    private sealed class FakeLookup(string providerKey) : IProviderDeliveryLookup
    {
        public string ProviderKey { get; } = providerKey;

        public Task<Result<IReadOnlyList<ProviderDeliveryEvent>>> LookupAsync(
            ProviderDeliveryQuery query,
            CancellationToken cancellationToken)
            => Task.FromResult(Result.Success<IReadOnlyList<ProviderDeliveryEvent>>([]));
    }
}
