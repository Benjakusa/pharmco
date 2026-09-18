using System.Security.Cryptography;
using Xunit;
using Pharmco.Client.Services;
using Pharmco.Core.Licensing;

namespace Pharmco.Client.Tests;

public class LicenseValidatorTests
{
    [Fact]
    public void License_SignatureVerified_ValidTokenPasses()
    {
        // Arrange
        using var rsa = RSA.Create(2048);
        var signer = new LicenseService(rsa);
        var validator = new LicenseValidator(rsa);
        var claims = new LicenseClaims
        {
            TenantId = Guid.NewGuid().ToString(),
            Code = "TEST-001",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(365),
            MaxUsers = 10,
            MaxTerminals = 5,
            Features = new() { "pos", "inventory" }
        };

        var token = signer.Sign(claims);

        // Act
        var result = validator.Verify(token);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(claims.TenantId, result.Claims?.TenantId);
        Assert.Equal(claims.Code, result.Claims?.Code);
    }

    [Fact]
    public void License_TamperedToken_Rejected()
    {
        // Arrange
        using var rsa = RSA.Create(2048);
        var signer = new LicenseService(rsa);
        var validator = new LicenseValidator(rsa);
        var claims = new LicenseClaims
        {
            TenantId = Guid.NewGuid().ToString(),
            Code = "TEST-001",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(365)
        };

        var token = signer.Sign(claims);
        var parts = token.Split('.');
        // Tamper with payload
        var tampered = $"{parts[0]}.{parts[1]}TAMPERED.{parts[2]}";

        // Act
        var result = validator.Verify(tampered);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("invalid", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void License_Expired30Days_Lock()
    {
        // Arrange
        using var rsa = RSA.Create(2048);
        var signer = new LicenseService(rsa);
        var validator = new LicenseValidator(rsa);
        var claims = new LicenseClaims
        {
            TenantId = Guid.NewGuid().ToString(),
            Code = "TEST-001",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-31)
        };

        var token = signer.Sign(claims);

        // Act
        var result = validator.Verify(token);

        // Assert
        Assert.True(result.IsValid); // Signature is valid, but expired
        Assert.True(result.IsExpired);
        Assert.True(result.DaysToExpiry <= -31);
    }

    [Fact]
    public void ClockTamper_Detected_BackwardsClockRejected()
    {
        // Arrange
        var lastSyncAt = DateTimeOffset.UtcNow;
        var validator = new LicenseValidator();

        // Simulate clock set 1 year in the past
        var tamperedClock = lastSyncAt.AddYears(-1);

        // Act
        var result = validator.DetectClockTamper(tamperedClock);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ClockTamper_NotDetected_NormalClockPasses()
    {
        // Arrange
        var lastSyncAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var validator = new LicenseValidator();

        // Act
        var result = validator.DetectClockTamper(lastSyncAt);

        // Assert
        Assert.False(result);
    }
}
