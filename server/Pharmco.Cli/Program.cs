using Pharmco.Core.Provisioning;

namespace Pharmco.Cli;

/// <summary>
/// Pharmco.Cli — tenant provisioning tool.
///
/// Commands:
///   provision-tenant     --code PHARMCO-XXX --name "..." --owner-phone 2547...
///   renew-license        --code PHARMCO-XXX [--days 365]
///   list-tenants
///   deprovision-tenant   --code PHARMCO-XXX --confirm PHARMCO-XXX
///   init-db              apply the idempotent master schema (bootstrap)
///   gen-keys             --out-dir .   (one-time RSA keypair generation)
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "provision-tenant" => await Commands.RunProvisionAsync(args[1..]),
                "renew-license" => await Commands.RunRenewAsync(args[1..]),
                "list-tenants" => await Commands.RunListAsync(),
                "deprovision-tenant" => await Commands.RunDeprovisionAsync(args[1..]),
                "init-db" => await Commands.RunInitDbAsync(),
                "gen-keys" => Commands.RunGenKeys(args[1..]),
                _ => UsageError($"unknown command: {args[0]}"),
            };
        }
        catch (ProvisioningException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (Npgsql.NpgsqlException ex)
        {
            Console.Error.WriteLine($"database error: {ex.Message}");
            return 1;
        }
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"error: {message}\n");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Pharmco.Cli — tenant provisioning

            usage:
              Pharmco.Cli provision-tenant --code PHARMCO-XXX --name "Name" --owner-phone 2547...
              Pharmco.Cli renew-license --code PHARMCO-XXX [--days 365]
              Pharmco.Cli list-tenants
              Pharmco.Cli deprovision-tenant --code PHARMCO-XXX --confirm PHARMCO-XXX
              Pharmco.Cli init-db
              Pharmco.Cli gen-keys [--out-dir ./keys]

            environment:
              ConnectionStrings__Master   Postgres connection string (required for DB commands)
              License__PrivateKeyPath     path to license_private.pem (required to sign)
            """);
    }
}