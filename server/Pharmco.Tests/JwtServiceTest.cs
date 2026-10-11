using Pharmco.Api.Services;
using Pharmco.Core.Auth;
using Pharmco.Core.Tenants;
using Xunit;

namespace Pharmco.Tests;

public class JwtServiceTest
{
    private static readonly string Secret = "unit-test-secret-0123456789abcdef0123456789abcdef";

    private static JwtService NewJwt() => new JwtService(Secret, new JwtConfig());

    [Fact]
    public void Jwt_ContainsTenantClaim()
    {
        var jwt = NewJwt();
        var token = jwt.IssueAccessToken(NewUser("admin"), NewTenant("PHARMCO-001", 4_100_000_000L));
        var claims = jwt.VerifyAccessToken(token);

        Assert.Equal("PHARMCO-001", claims.TenantCode);
        Assert.Equal("admin", claims.Role);
        Assert.NotEqual(Guid.Empty, claims.UserId);
        Assert.NotEqual(Guid.Empty, claims.TenantId);
        // access token lifetime = 15 minutes
        Assert.Equal(15L * 60, claims.ExpiresAt.ToUnixTimeSeconds() - claims.IssuedAt.ToUnixTimeSeconds());
        // license claim rides along
        Assert.Equal(4_100_000_000L, claims.LicenseExpiresAtEpoch);
    }

    [Fact]
    public void Jwt_TamperedSignature_Rejected()
    {
        var token = NewJwt().IssueAccessToken(NewUser("cashier"), NewTenant("PHARMCO-001", null));
        var parts = token.Split('.');
        var payload = parts[1];
        var flipped = payload.StartsWith('A') ? "B" + payload.Substring(1) : "A" + payload.Substring(1);
        var tampered = parts[0] + "." + flipped + "." + parts[2];

        Assert.Throws<JwtException>(() => NewJwt().VerifyAccessToken(tampered));
    }

    [Fact]
    public void Jwt_WrongKey_Rejected()
    {
        var token = NewJwt().IssueAccessToken(NewUser("admin"), NewTenant("PHARMCO-001", null));
        var other = new JwtService("a-different-secret-0123456789abcdef0123456789", new JwtConfig());
        Assert.Throws<JwtException>(() => other.VerifyAccessToken(token));
    }

    [Fact]
    public void RefreshToken_RoundTrip_HashedNotRaw()
    {
        var raw = JwtService.GenerateRefreshToken();
        var hash = JwtService.HashToken(raw);

        Assert.Equal(64, raw.Length);            // 64 random bytes → base64url
        Assert.Equal(64, hash.Length);           // SHA-256 hex
        Assert.DoesNotContain(raw, hash);        // DB never sees the raw token
    }

    // --- fixtures -----------------------------------------------------------------

    private static User NewUser(string role)
    {
        return new User
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Username = "admin@nairobi-chemist",
            PasswordHash = "$2b$12$demo",
            Role = role,
            IsActive = true,
        };
    }

    private static Tenant NewTenant(string code, long? licenseExpiresEpoch)
    {
        return new Tenant
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Code = code,
            Status = "active",
            SchemaName = "tenant_" + code.ToLower(),
            LicenseExpiresAt = licenseExpiresEpoch is null
                ? null
                : DateTimeOffset.FromUnixTimeSeconds(licenseExpiresEpoch.Value),
        };
    }
}
