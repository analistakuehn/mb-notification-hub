using NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class ProviderWebhookInterpreterResolverTests
{
    [Fact]
    public void Resolves_the_interpreter_registered_under_each_provider_key()
    {
        ProviderWebhookInterpreterResolver resolver = new(
            [new FakeInterpreter("twilio"), new FakeInterpreter("sendgrid")]);

        Result<IProviderWebhookInterpreter> twilio = resolver.Resolve("twilio");
        Result<IProviderWebhookInterpreter> sendgrid = resolver.Resolve("sendgrid");

        twilio.IsSuccess.ShouldBeTrue(twilio.Error);
        twilio.Value!.ProviderKey.ShouldBe("twilio");
        sendgrid.IsSuccess.ShouldBeTrue(sendgrid.Error);
        sendgrid.Value!.ProviderKey.ShouldBe("sendgrid");
    }

    [Fact]
    public void Refuses_a_key_no_hosted_interpreter_speaks_for()
    {
        ProviderWebhookInterpreterResolver resolver = new([new FakeInterpreter("twilio")]);

        Result<IProviderWebhookInterpreter> result = resolver.Resolve("acme-mail");

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.ProviderUnknown).ShouldBeTrue(result.Error);
        result.ErrorKind.ShouldBe(ResultErrorKind.NotFound);
    }

    [Fact]
    public void Refuses_a_key_that_differs_only_by_case_because_matching_is_ordinal()
    {
        ProviderWebhookInterpreterResolver resolver = new([new FakeInterpreter("twilio")]);

        Result<IProviderWebhookInterpreter> result = resolver.Resolve("Twilio");

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.ProviderUnknown).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Refuses_a_blank_key_instead_of_faulting_on_untrusted_input()
    {
        ProviderWebhookInterpreterResolver resolver = new([new FakeInterpreter("twilio")]);

        Result<IProviderWebhookInterpreter> result = resolver.Resolve("   ");

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.ProviderUnknown).ShouldBeTrue(result.Error);
    }

    private sealed class FakeInterpreter(string providerKey) : IProviderWebhookInterpreter
    {
        public string ProviderKey => providerKey;

        public bool SignatureCoversRoute => false;

        public Result<VerifiedProviderWebhook> Verify(ProviderWebhookRequest request)
            => Result.Success(new VerifiedProviderWebhook(
                providerKey, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty));

        public Result<IReadOnlyList<ProviderDeliveryEvent>> Interpret(VerifiedProviderWebhook webhook)
            => Result.Success<IReadOnlyList<ProviderDeliveryEvent>>([]);
    }
}
