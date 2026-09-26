using Xunit;

namespace Pharmco.Tests;

/// <summary>
/// A [LiveStackFact] is skipped at discovery time unless PHARMCO_TEST_API_URL
/// points at a running API (these tests exercise the full auth flow end-to-end).
/// </summary>
public sealed class LiveStackFactAttribute : FactAttribute
{
    public LiveStackFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PHARMCO_TEST_API_URL")))
            Skip = "PHARMCO_TEST_API_URL is not set — live-stack test skipped";
    }
}
