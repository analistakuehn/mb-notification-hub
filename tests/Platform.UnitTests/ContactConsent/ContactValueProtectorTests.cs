using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;

namespace NotificationHub.UnitTests.ContactConsent;

public sealed class ContactValueProtectorTests
{
    private static readonly string MasterKey =
        Convert.ToBase64String(Enumerable.Repeat((byte)0x5a, 32).ToArray());

    private static ContactValueProtector CreateProtector(string masterKey)
    {
        IOptions<EnvelopeCipherOptions> options = Options.Create(new EnvelopeCipherOptions
        {
            KeyId = "unit-test-key",
            MasterKey = masterKey,
        });
        return new ContactValueProtector(new LocalKeyEnvelopeCipher(options), options);
    }

    [Fact]
    public void The_hash_of_a_value_is_deterministic_and_lowercase_hex()
    {
        ContactValueProtector protector = CreateProtector(MasterKey);

        var first = protector.Hash("cliente@example.com");
        var second = protector.Hash("cliente@example.com");

        first.ShouldBe(second);
        first.Length.ShouldBe(64);
        first.ShouldAllBe(character => char.IsAsciiHexDigitLower(character));
        protector.Hash("outra@example.com").ShouldNotBe(first);
    }

    [Fact]
    public void The_hash_is_keyed_and_never_a_plain_digest_of_the_value()
    {
        ContactValueProtector protector = CreateProtector(MasterKey);

        var keyed = protector.Hash("cliente@example.com");
        var plain = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("cliente@example.com")));

        keyed.ShouldNotBe(plain);
    }

    [Fact]
    public void A_different_master_key_produces_a_different_hash()
    {
        ContactValueProtector first = CreateProtector(MasterKey);
        ContactValueProtector second = CreateProtector(
            Convert.ToBase64String(Enumerable.Repeat((byte)0x11, 32).ToArray()));

        first.Hash("cliente@example.com").ShouldNotBe(second.Hash("cliente@example.com"));
    }

    [Fact]
    public async Task A_protected_value_round_trips_to_the_original_plaintext()
    {
        ContactValueProtector protector = CreateProtector(MasterKey);

        ProtectedContactValue protectedValue = await protector.ProtectAsync(
            "cliente@example.com", CancellationToken.None);
        var revealed = await protector.RevealAsync(protectedValue.Encrypted, CancellationToken.None);

        revealed.ShouldBe("cliente@example.com");
        protectedValue.Encrypted.ShouldNotBe(Encoding.UTF8.GetBytes("cliente@example.com"));
        protectedValue.Hash.ShouldBe(protector.Hash("cliente@example.com"));
    }

    [Fact]
    public async Task An_envelope_of_the_module_scope_never_opens_under_an_application_scope()
    {
        IOptions<EnvelopeCipherOptions> options = Options.Create(new EnvelopeCipherOptions
        {
            KeyId = "unit-test-key",
            MasterKey = MasterKey,
        });
        var cipher = new LocalKeyEnvelopeCipher(options);
        ContactValueProtector protector = new(cipher, options);

        ProtectedContactValue protectedValue = await protector.ProtectAsync(
            "+5511999990000", CancellationToken.None);

        await Should.ThrowAsync<CryptographicException>(
            () => cipher.DecryptAsync("araia-cambio", protectedValue.Encrypted, CancellationToken.None));
    }
}
