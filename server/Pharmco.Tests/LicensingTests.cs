using Pharmco.Core.Licensing;
using Pharmco.Core.Provisioning;
using Xunit;

namespace Pharmco.Tests;

public class LicensingTests
{
    private static LicenseClaims NewClaims(DateTimeOffset expiresAt)
        => new()
        {
            TenantId = "0997f33c-7f5f-4b78-a3b1-9d9e31b5f9d2",
            Code = "PHARMCO-001",
            ExpiresAt = expiresAt,
            MaxUsers = 10,
            MaxTerminals = 5,
            Features = new() { "pos", "inventory", "mpesa" },
        };

    [Fact]
    public void Sign_ThenVerify_ReturnsOriginalClaims()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(365);
        var token = TestKeys.Signer.Sign(NewClaims(expiresAt));

        var verified = TestKeys.Signer.Verify(token, TestKeys.PublicKey);

        Assert.Equal("PHARMCO-001", verified.Code);
        Assert.Equal("0997f33c-7f5f-4b78-a3b1-9d9e31b5f9d2", verified.TenantId);
        Assert.Equal(10, verified.MaxUsers);
        Assert.Equal(5, verified.MaxTerminals);
        Assert.Equal(expiresAt, verified.ExpiresAt, TimeSpan.FromSeconds(5));
        Assert.Contains("mpesa", verified.Features);
    }

    [Fact]
    public void Verify_TamperedToken_Throws()
    {
        var token = TestKeys.Signer.Sign(NewClaims(DateTimeOffset.UtcNow.AddDays(365)));
        // flip one payload character without breaking base64url structure
        var parts = token.Split('.');
        var fakePayload = parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');
        var forged = $"{parts[0]}.{fakePayload}.{parts[2]}";

        var ex = Assert.Throws<LicenseException>(() => TestKeys.Signer.Verify(forged, TestKeys.PublicKey));
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_ExpiredToken_Throws()
    {
        var token = TestKeys.Signer.Sign(NewClaims(DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Throws<LicenseException>(() => TestKeys.Signer.Verify(token, TestKeys.PublicKey));
    }

    [Fact]
    public void RenewalPolicy_ExtendsFromCurrentExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var current = now.AddDays(30);
        var next = RenewalPolicy.NextExpiry(current, 365, now);
        Assert.Equal(current.AddDays(365), next);
    }

    [Fact]
    public void RenewalPolicy_ExtendsFromToday_WhenAnnualExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var next = RenewalPolicy.NextExpiry(now.AddDays(-400), 365, now);
        Assert.Equal(now.AddDays(365), next, TimeSpan.FromSeconds(5));
    }
}