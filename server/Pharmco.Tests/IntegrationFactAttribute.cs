using Xunit;

namespace Pharmco.Tests;

/// <summary>
/// An [IntegrationFact] is skipped at discovery time unless
/// PHARMCO_TEST_CONNECTION points at a disposable test database.
/// Unit tests ([Fact]) never need it.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PHARMCO_TEST_CONNECTION")))
            Skip = "PHARMCO_TEST_CONNECTION is not set — integration test skipped";
    }
}