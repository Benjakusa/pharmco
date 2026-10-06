using Xunit;
using Pharmco.Client.Services;
using Pharmco.Core.Sales;

namespace Pharmco.Client.Tests;

public class OfflineSaleTests
{
    [Fact]
    public async Task OfflineSale_QueuedInSyncQueue()
    {
        // Arrange
        using var localDb = new LocalDatabase(":memory:");
        var sale = new InFlightSale
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            CashierUserId = Guid.NewGuid(),
            TotalCents = 5000,
            PaymentMode = PaymentMode.Cash,
            CustomerPhone = "254712345678",
            Lines = new List<InFlightLine>
            {
                new() { ProductId = Guid.NewGuid(), Qty = 2, UnitPriceCents = 2500 }
            }
        };

        // Act
        await localDb.InsertSaleAsync(sale);
        var queue = await localDb.GetPendingOperationsAsync(maxCount: 10);

        // Assert
        Assert.NotEmpty(queue);
        Assert.Equal("sale", queue.First().Entity);
        Assert.Equal("create", queue.First().Operation);
    }

    [Fact]
    public async Task OfflineSale_MultipleSales_QueuedCorrectly()
    {
        // Arrange
        using var localDb = new LocalDatabase(":memory:");
        var sales = Enumerable.Range(0, 10).Select(_ => new InFlightSale
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            CashierUserId = Guid.NewGuid(),
            TotalCents = Random.Shared.Next(1000, 10000),
            PaymentMode = PaymentMode.Cash,
            Lines = new List<InFlightLine>
            {
                new() { ProductId = Guid.NewGuid(), Qty = 1, UnitPriceCents = 500 }
            }
        }).ToList();

        // Act
        foreach (var sale in sales)
        {
            await localDb.InsertSaleAsync(sale);
        }

        var queue = await localDb.GetPendingOperationsAsync(maxCount: 100);

        // Assert
        Assert.Equal(10, queue.Count);
    }

    [Fact]
    public async Task SyncQueue_MarkSynced_RemovesFromPending()
    {
        // Arrange
        using var localDb = new LocalDatabase(":memory:");
        var sale = new InFlightSale
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            CashierUserId = Guid.NewGuid(),
            TotalCents = 5000,
            PaymentMode = PaymentMode.Cash,
            Lines = new List<InFlightLine>
            {
                new() { ProductId = Guid.NewGuid(), Qty = 1, UnitPriceCents = 5000 }
            }
        };

        await localDb.InsertSaleAsync(sale);
        var queue = await localDb.GetPendingOperationsAsync(maxCount: 10);
        var uuid = queue.First().Uuid;

        // Act
        await localDb.MarkOperationSyncedAsync(uuid);
        var pendingAfter = await localDb.GetPendingOperationsAsync(maxCount: 10);

        // Assert
        Assert.Empty(pendingAfter);
    }
}
