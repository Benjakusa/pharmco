using System.Security.Cryptography;
using Pharmco.Core.Data;
using Pharmco.Core.Licensing;
using Pharmco.Core.Provisioning;
using Pharmco.Core.Tenants;

namespace Pharmco.Cli;

internal static class Commands
{
    // ---------------------------------------------------------------- commands

    public static async Task<int> RunProvisionAsync(string[] args)
    {
        var opt = Options.Parse(args);
        var code = opt.Required("code");
        var name = opt.Required("name");
        var phone = opt.Required("owner-phone");
        var admin = opt.Optional("admin-username");
        var days = opt.OptionalInt("license-days") ?? 365;

        var provisioner = CreateProvisioner(needSigningKey: true);
        var result = await provisioner.ProvisionAsync(new ProvisionRequest(code, name, phone, admin, days));

        Console.WriteLine($"Tenant code:    {result.Code}");
        Console.WriteLine($"Schema name:    {result.SchemaName}");
        Console.WriteLine($"Admin username: {result.AdminUsername}");
        Console.WriteLine($"Temp password:  {result.TempPassword}");
        Console.WriteLine($"License expiry: {Format(result.LicenseExpiresAt)}");
        Console.WriteLine($"License key:    {result.LicenseKey}");
        Console.WriteLine();
        Console.WriteLine("✓ tenant provisioned (admin must change the temp password on first login)");
        return 0;
    }

    public static async Task<int> RunRenewAsync(string[] args)
    {
        var opt = Options.Parse(args);
        var code = opt.Required("code");
        var days = opt.OptionalInt("days") ?? 365;

        var provisioner = CreateProvisioner(needSigningKey: true);
        var newExpiry = await provisioner.RenewLicenseAsync(code, days);

        Console.WriteLine($"Tenant code:    {code}");
        Console.WriteLine($"New expiry:     {Format(newExpiry)}");
        Console.WriteLine("✓ license renewed and re-signed");
        return 0;
    }

    public static async Task<int> RunListAsync()
    {
        var provisioner = CreateProvisioner(needSigningKey: false);
        var tenants = await provisioner.ListAsync();

        Console.WriteLine($"{"CODE",-16} {"STATUS",-12} {"SCHEMA",-28} {"LICENSE EXPIRES AT",-22} NAME");
        foreach (var t in tenants)
        {
            Console.WriteLine(
                $"{t.Code,-16} {t.Status,-12} {t.SchemaName,-28} {(t.LicenseExpiresAt is { } exp ? Format(exp) : "n/a"),-22} {t.Name}");
        }
        return 0;
    }

    public static async Task<int> RunDeprovisionAsync(string[] args)
    {
        var opt = Options.Parse(args);
        var code = opt.Required("code");
        var confirm = opt.Required("confirm");

        var provisioner = CreateProvisioner(needSigningKey: false);
        await provisioner.DeprovisionAsync(code, confirm);

        Console.WriteLine($"Tenant code:    {code}");
        Console.WriteLine("✓ tenant deprovisioned (schema dropped, master row inactive, audit logged)");
        return 0;
    }

    public static async Task<int> RunInitDbAsync()
    {
        await using var conn = await ConnectionFactory().OpenAsync();
        await SchemaInstaller.ApplyMasterAsync(conn);
        Console.WriteLine("✓ master schema applied (idempotent)");
        return 0;
    }

    public static int RunGenKeys(string[] args)
    {
        var opt = Options.Parse(args);
        var dir = opt.Optional("out-dir") ?? ".";
        Directory.CreateDirectory(dir);

        using var rsa = RSA.Create(2048);
        var privatePem = rsa.ExportRSAPrivateKeyPem();
        var publicPem = rsa.ExportRSAPublicKeyPem();

        var privatePath = Path.Combine(dir, "license_private.pem");
        var publicPath = Path.Combine(dir, "license_public.pem");
        File.WriteAllText(privatePath, privatePem);
        File.WriteAllText(publicPath, publicPem);

        Console.WriteLine($"✓ wrote {privatePath} (chmod 600; stays on the server)");
        Console.WriteLine($"✓ wrote {publicPath}  (embed in the desktop client)");
        return 0;
    }

    // ---------------------------------------------------------------- plumbing

    private static NpgsqlConnectionFactory ConnectionFactory()
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Master");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("ConnectionStrings__Master is required");
        return new NpgsqlConnectionFactory(cs);
    }

    private static TenantProvisioner CreateProvisioner(bool needSigningKey)
    {
        var factory = ConnectionFactory();
        var repository = new TenantRepository(factory);
        // listing/deprovision never signs; hand a throwaway key so the ctor stays uniform
        var license = needSigningKey ? new LicenseService(LoadSigningKey()) : new LicenseService(RSA.Create(2048));
        return new TenantProvisioner(factory, repository, license);
    }

    private static RSA LoadSigningKey()
    {
        var path = Environment.GetEnvironmentVariable("License__PrivateKeyPath");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(
                "License__PrivateKeyPath must point to a license_private.pem (run: Pharmco.Cli gen-keys)");
        return LicenseService.LoadPrivateKey(File.ReadAllText(path));
    }

    private static string Format(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
}