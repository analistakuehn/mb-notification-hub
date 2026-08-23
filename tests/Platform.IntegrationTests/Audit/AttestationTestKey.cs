namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// Fixed NIST P-256 pair used by the tests that sign locally. It is committed
/// on purpose and only ever signs test artifacts: the key id carries the
/// dev-only marker the hosts refuse to boot with outside development.
/// </summary>
internal static class AttestationTestKey
{
    /// <summary>PKCS#8 private key, base64.</summary>
    internal const string PrivateKeyBase64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgJlTwSPSOMZJSqkeUexDqKEIo5nh2"
        + "oWJcKTZ5YCXT8NehRANCAASoZWiwNFThfecCCgQQJEWJnYXoJKE0QGnBSFM2XytFGdRYMAJsB8Sn"
        + "D1n6NpjUMecTKs0TKHJ1qCgNVdQa2bV8";
}
