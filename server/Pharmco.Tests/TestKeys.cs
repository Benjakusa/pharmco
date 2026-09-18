using System.Security.Cryptography;
using Pharmco.Core.Licensing;

namespace Pharmco.Tests;

/// <summary>
/// Shared RSA-2048 test keypair. Signing and verifying use SEPARATE RSA
/// instances derived from the same keypair, so tests prove the full
/// cross-instance round-trip (server signs, client verifies).
/// </summary>
internal static class TestKeys
{
    static TestKeys()
    {
        using var rsa = RSA.Create(2048);
        var privatePem = rsa.ExportRSAPrivateKeyPem();
        var publicPem = rsa.ExportRSAPublicKeyPem();
        Signer = new LicenseService(LicenseService.LoadPrivateKey(privatePem));
        PublicKey = LicenseService.LoadPublicKey(publicPem);
    }

    public static LicenseService Signer { get; }
    public static RSA PublicKey { get; }
}