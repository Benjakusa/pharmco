using Xunit;
using Pharmco.Client.Services;
using Pharmco.Core.Sales;

namespace Pharmco.Client.Tests;

public class SyncEngineTests
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
            Lines = new List<InFlightLine>
            {
                new() { ProductId = Guid.NewGuid(), Qty = 2, UnitPriceCents = 2500 }
            }
        };

        // Act
        await localDb.InsertSaleAsync(sale);
        var queueCount = CountPendingOperations(localDb);

        // Assert
        Assert.Equal(1, queueCount);
    }

    [Fact]
    public void Sync_BackoffOnFailure_RetriesWithCorrectIntervals()
    {
        // The backoff schedule should be: 1s, 5s, 30s, 5m, 15m
        var expectedBackoffs = new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15)
        };

        Assert.Equal(expectedBackoffs.Length, SyncEngine.BackoffSchedule.Length);
        for (int i = 0; i < expectedBackoffs.Length; i++)
        {
            Assert.Equal(expectedBackoffs[i], SyncEngine.BackoffSchedule[i]);
        }
    }

    private static int CountPendingOperations(LocalDatabase db)
    {
        using var cmd = db.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sync_queue WHERE synced_at IS NULL";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
