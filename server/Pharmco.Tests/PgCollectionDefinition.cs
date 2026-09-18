using Xunit;

namespace Pharmco.Tests;

/// <summary>One shared PgFixture for all DB-backed tests (serial execution).</summary>
[CollectionDefinition("pg")]
public sealed class PgCollectionDefinition : ICollectionFixture<PgFixture>
{
}