using System.Text.Json;
using Xunit;
using Pharmco.Client.Services;

namespace Pharmco.Client.Tests;

public class LicenseEnforcementTests
{
    [Fact]
    public void LicenseTier_Normal_NoRestriction()
    {
        // Arrange
        var service = new LicenseEnforcementService(null!, new LicenseValidator(), null!);
        var state = new LicenseEnforcementState
        {
            IsValid = true,
            DaysToExpiry = 60,
            Tier = LicenseEnforcementService.LicenseTier.Normal
        };

        // Act
        var canSale = service.IsSaleAllowed();

        // Assert
        Assert.True(canSale);
    }

    [Fact]
    public void LicenseTier_GracePeriod_AllowsSalesWithWarning()
    {
        // Arrange
        var service = new LicenseEnforcementService(null!, new LicenseValidator(), null!);
        var state = new LicenseEnforcementState
        {
            IsValid = true,
            DaysToExpiry = -5,
            Tier = LicenseEnforcementService.LicenseTier.GracePeriod
        };

        // Act
        var canSale = service.IsSaleAllowed();

        // Assert
        Assert.True(canSale); // Grace period allows sales with warning
    }

    [Fact]
    public void LicenseTier_ReadOnly_BlocksSales()
    {
        // Arrange
        var service = new LicenseEnforcementService(null!, new LicenseValidator(), null!);
        var state = new LicenseEnforcementState
        {
            IsValid = true,
            DaysToExpiry = -20,
            Tier = LicenseEnforcementService.LicenseTier.ReadOnly
        };

        // Act
        var canSale = service.IsSaleAllowed();

        // Assert
        Assert.False(canSale);
    }

    [Fact]
    public void LicenseTier_HardLock_BlocksSales()
    {
        // Arrange
        var service = new LicenseEnforcementService(null!, new LicenseValidator(), null!);
        var state = new LicenseEnforcementState
        {
            IsValid = false,
            Error = "License expired beyond grace period",
            Tier = LicenseEnforcementService.LicenseTier.HardLock
        };

        // Act
        var canSale = service.IsSaleAllowed();

        // Assert
        Assert.False(canSale);
    }

    [Fact]
    public void LicenseTier_WarningYellow_ShowsBanner()
    {
        // Arrange
        var service = new LicenseEnforcementService(null!, new LicenseValidator(), null!);
        var state = new LicenseEnforcementState
        {
            IsValid = true,
            DaysToExpiry = 20,
            Tier = LicenseEnforcementService.LicenseTier.WarningYellow
        };

        // Act
        var message = service.GetWarningMessage();

        // Assert
        Assert.Contains("renews in", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("20 days", message);
    }

    [Fact]
    public void LicenseTier_WarningRed_ShowsBanner()
    {
        // Arrange
        var service = new LicenseEnforcementService(null!, new LicenseValidator(), null!);
        var state = new LicenseEnforcementState
        {
            IsValid = true,
            DaysToExpiry = 3,
            Tier = LicenseEnforcementService.LicenseTier.WarningRed
        };

        // Act
        var message = service.GetWarningMessage();

        // Assert
        Assert.Contains("expires in", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 days", message);
    }

    [Fact]
    public void RenewalInfo_ReturnsCorrectPaybillDetails()
    {
        // Arrange
        var service = new LicenseEnforcementService(null!, new LicenseValidator(), null!);
        var state = new LicenseEnforcementState
        {
            TenantCode = "PHARMCO-001"
        };

        // Act
        var info = service.GetRenewalInfo();

        // Assert
        Assert.Equal("500000", info.PaybillNumber);
        Assert.Equal("PHARMCO-REHISTRY", info.AccountReference);
        Assert.Equal(5000, info.Amount);
        Assert.Equal("+254700000000", info.Phone);
        Assert.Equal("PHARMCO-001", info.TenantCode);
    }
}
