using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Cryptography;

namespace NotificationHub.UnitTests.Infrastructure;

public sealed class LocalKeyEnvelopeCipherTests
{
    private const string KeyId = "unit-test-key";

    private static LocalKeyEnvelopeCipher CreateCipher(string keyId = KeyId, byte? masterKeyByte = null)
        => new(Options.Create(new EnvelopeCipherOptions
        {
            KeyId = keyId,
            MasterKey = Convert.ToBase64String(
                Enumerable.Repeat(masterKeyByte ?? 0x5a, 32).ToArray()),
        }));

    [Fact]
    public async Task An_envelope_round_trips_to_the_original_plaintext()
    {
        LocalKeyEnvelopeCipher cipher = CreateCipher();
        var plaintext = Encoding.UTF8.GetBytes("""{"code":"482913","expiresInMinutes":5}""");

        var envelope = await cipher.EncryptAsync("araia-cambio", plaintext, CancellationToken.None);
        var decrypted = await cipher.DecryptAsync("araia-cambio", envelope, CancellationToken.None);

        decrypted.ShouldBe(plaintext);
        envelope.ShouldNotBe(plaintext);
    }

    [Fact]
    public async Task The_envelope_carries_the_format_version_and_the_key_id()
    {
        LocalKeyEnvelopeCipher cipher = CreateCipher();

        var envelope = await cipher.EncryptAsync(
            "araia-cambio", Encoding.UTF8.GetBytes("{}"), CancellationToken.None);

        envelope[0].ShouldBe((byte)1);
        var keyIdLength = (int)envelope[1];
        Encoding.UTF8.GetString(envelope, 2, keyIdLength).ShouldBe(KeyId);
    }

    [Fact]
    public async Task An_envelope_of_one_application_never_opens_for_another()
    {
        LocalKeyEnvelopeCipher cipher = CreateCipher();
        var envelope = await cipher.EncryptAsync(
            "araia-cambio", Encoding.UTF8.GetBytes("""{"code":"482913"}"""), CancellationToken.None);

        await Should.ThrowAsync<CryptographicException>(
            () => cipher.DecryptAsync("outra-aplicacao", envelope, CancellationToken.None));
    }

    [Fact]
    public async Task An_envelope_protected_by_an_unknown_key_id_is_refused_before_decryption()
    {
        LocalKeyEnvelopeCipher writer = CreateCipher(keyId: "rotated-away");
        LocalKeyEnvelopeCipher reader = CreateCipher();
        var envelope = await writer.EncryptAsync(
            "araia-cambio", Encoding.UTF8.GetBytes("{}"), CancellationToken.None);

        CryptographicException exception = await Should.ThrowAsync<CryptographicException>(
            () => reader.DecryptAsync("araia-cambio", envelope, CancellationToken.None));

        exception.Message.ShouldContain("rotated-away");
    }

    [Fact]
    public async Task A_tampered_ciphertext_fails_authentication()
    {
        LocalKeyEnvelopeCipher cipher = CreateCipher();
        var envelope = await cipher.EncryptAsync(
            "araia-cambio", Encoding.UTF8.GetBytes("""{"code":"482913"}"""), CancellationToken.None);
        envelope[^1] ^= 0xff;

        await Should.ThrowAsync<CryptographicException>(
            () => cipher.DecryptAsync("araia-cambio", envelope, CancellationToken.None));
    }

    [Fact]
    public async Task A_different_master_key_never_opens_the_envelope()
    {
        LocalKeyEnvelopeCipher writer = CreateCipher();
        LocalKeyEnvelopeCipher reader = CreateCipher(masterKeyByte: 0x11);
        var envelope = await writer.EncryptAsync(
            "araia-cambio", Encoding.UTF8.GetBytes("{}"), CancellationToken.None);

        await Should.ThrowAsync<CryptographicException>(
            () => reader.DecryptAsync("araia-cambio", envelope, CancellationToken.None));
    }

    [Fact]
    public void A_master_key_under_32_bytes_is_rejected_at_construction()
        => Should.Throw<InvalidOperationException>(() => new LocalKeyEnvelopeCipher(
            Options.Create(new EnvelopeCipherOptions
            {
                KeyId = KeyId,
                MasterKey = Convert.ToBase64String(new byte[16]),
            })));
}
